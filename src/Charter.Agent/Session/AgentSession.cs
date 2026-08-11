using System.Globalization;
using Charter.Agent.Capabilities;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;

namespace Charter.Agent.Session;

/// <summary>Where a connection has got to.</summary>
public enum SessionPhase
{
    /// <summary>Hello sent, welcome not yet received.</summary>
    Handshaking,

    /// <summary>Version agreed. Work may be claimed.</summary>
    Ready,

    /// <summary>
    /// Version not agreed (section 33.6). The agent stays connected so the operator can see it in
    /// the runners list with the reason, and claims nothing.
    /// </summary>
    Refusing,

    Closed,
}

/// <summary>A log line the session wants written. Already free of secrets.</summary>
public sealed record SessionNote(LogLevel Level, string Message);

/// <summary>Why the connection should end.</summary>
public sealed record SessionClose(string Reason, bool CredentialRevoked = false, bool Fatal = false);

/// <summary>
/// Everything the daemon should do as a result of one input.
/// </summary>
/// <param name="Send">Frames to put on the wire, in order.</param>
/// <param name="Start">Jobs to hand to the executor.</param>
/// <param name="Stop">Jobs to stop.</param>
/// <param name="Notes">Lines to log.</param>
/// <param name="ReprobeRequested">Run the capability probes and call back with the result.</param>
/// <param name="Close">Set when this connection should end.</param>
/// <param name="NextWakeAt">
/// The latest instant the daemon may sit idle until. Receiving a frame earlier is fine; sleeping
/// past this is not, because a heartbeat or a lease deadline falls on it.
/// </param>
public sealed record SessionStep(
    IReadOnlyList<Envelope> Send,
    IReadOnlyList<JobAssignment> Start,
    IReadOnlyList<JobStop> Stop,
    IReadOnlyList<SessionNote> Notes,
    bool ReprobeRequested,
    SessionClose? Close,
    DateTimeOffset NextWakeAt);

/// <summary>
/// The agent's half of the protocol, as a state machine (sections 33.3, 33.4, 33.6).
/// </summary>
/// <remarks>
/// Every decision the daemon makes about time — heartbeat due, claim due, lease lapsed, daily
/// re-probe due — is taken here from a <c>now</c> passed in by the caller, and every effect comes
/// back as data. That is what makes leases, backoff, version refusal and capability filtering
/// testable without a clock, a socket, or a control plane.
/// <para>
/// The session claims; it is never pushed to. It asks for at most the number of free slots it has,
/// re-checks every granted job against its own capability set, and hands back anything it cannot
/// run rather than failing it three minutes later.
/// </para>
/// </remarks>
public sealed class AgentSession
{
    private readonly AgentOptions _options;
    private readonly LeaseBook _leases = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    private CapabilitySet _capabilities;
    private string? _outstandingHeartbeatId;
    private IReadOnlyList<string> _outstandingHeartbeatJobIds = [];
    private DateTimeOffset _nextHeartbeatAt;
    private DateTimeOffset _nextClaimAt;
    private DateTimeOffset _nextReprobeAt;
    private DateTimeOffset _lastAckAt;
    private bool _reprobeInFlight;
    private string? _outstandingClaimId;
    private DateTimeOffset _outstandingClaimExpiresAt;

    public AgentSession(AgentOptions options, CapabilitySet capabilities, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);

        _options = options;
        _capabilities = capabilities;
        _nextHeartbeatAt = now + options.HeartbeatInterval;
        _nextClaimAt = now;
        _nextReprobeAt = now + options.ReprobeInterval;
        _lastAckAt = now;
    }

    public SessionPhase Phase { get; private set; } = SessionPhase.Handshaking;

    public string? AgentId { get; private set; }

    /// <summary>Present once a version could not be agreed. The reason work is being refused.</summary>
    public string? RefusalReason { get; private set; }

    public TimeSpan HeartbeatInterval { get; private set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseTtl { get; private set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ClaimInterval { get; private set; } = TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> Capabilities => _capabilities.Capabilities;

    public IReadOnlyList<string> HeldJobIds => _leases.HeldJobIds;

    /// <summary>Free concurrency slots. Never negative, and never more than <c>--concurrency</c>.</summary>
    public int AvailableSlots => Math.Max(0, _options.Concurrency - _running.Count);

    /// <summary>The first frame on a new connection.</summary>
    public SessionStep Open(DateTimeOffset now)
    {
        Phase = SessionPhase.Handshaking;
        _nextHeartbeatAt = now + HeartbeatInterval;
        _outstandingClaimId = null;
        _outstandingHeartbeatId = null;
        _outstandingHeartbeatJobIds = [];

        var hello = Envelope.Create(
            MessageTypes.Hello,
            new HelloPayload
            {
                ProtocolVersion = AgentProtocol.Version,
                SupportedProtocolVersions = AgentProtocol.SupportedVersions,
                AgentVersion = AgentBuild.Version,
                Name = _options.Name,
                Mode = _options.Mode.ToWire(),
                Concurrency = _options.Concurrency,
                Platform = AgentBuild.Platform(),
                Capabilities = _capabilities.Capabilities,
                CapabilitiesProbedAt = _capabilities.ProbedAt,
                HeldJobIds = _leases.HeldJobIds,
            },
            now);

        return Step(now, send: [hello]);
    }

    /// <summary>Time-driven work: heartbeat, claim, lapsed leases, the daily re-probe.</summary>
    public SessionStep Advance(DateTimeOffset now)
    {
        var send = new List<Envelope>();
        var stop = new List<JobStop>();
        var notes = new List<SessionNote>();
        var reprobe = false;

        // A lapsed lease means the control plane has, or shortly will have, re-queued the job. Stop
        // the local work first and do not report a result for it: whoever claims it next owns it now.
        foreach (var jobId in _leases.TakeExpired(now))
        {
            _running.Remove(jobId);
            stop.Add(new JobStop(jobId, "the lease expired before it could be renewed", Report: false));
            notes.Add(new SessionNote(
                LogLevel.Warning,
                $"job {jobId}: lease expired, stopping local work - the control plane will re-queue it"));
        }

        if (Phase != SessionPhase.Closed && now >= _nextHeartbeatAt)
        {
            send.Add(Heartbeat(now));
            _nextHeartbeatAt = now + HeartbeatInterval;
        }

        if (Phase == SessionPhase.Ready && now >= _nextReprobeAt && !_reprobeInFlight)
        {
            _reprobeInFlight = true;
            reprobe = true;
            notes.Add(new SessionNote(LogLevel.Info, "re-probing host capabilities"));
        }

        if (CanClaim(now))
        {
            send.Add(Claim(now));
        }

        return Step(now, send, stop: stop, notes: notes, reprobe: reprobe);
    }

    /// <summary>A frame from the control plane.</summary>
    public SessionStep Receive(Envelope envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Type switch
        {
            MessageTypes.Welcome => OnWelcome(envelope, now),
            MessageTypes.ProtocolMismatch => OnProtocolMismatch(envelope, now),
            MessageTypes.HeartbeatAck => OnHeartbeatAck(envelope, now),
            MessageTypes.JobGrant => OnJobGrant(envelope, now),
            MessageTypes.JobCancel => OnJobCancel(envelope, now),
            MessageTypes.Revoked => OnRevoked(envelope, now),
            MessageTypes.Error => OnError(envelope, now),
            _ => Step(now, notes: [new SessionNote(
                LogLevel.Debug,
                $"ignoring unknown message type '{envelope.Type}' from the control plane")]),
        };
    }

    /// <summary>The executor finished a job. Frees the slot and reports the outcome.</summary>
    public SessionStep JobFinished(JobCompletion completion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completion);

        _running.Remove(completion.JobId);
        var held = _leases.Release(completion.JobId);

        var send = new List<Envelope>();
        if (held)
        {
            send.Add(Envelope.Create(
                MessageTypes.JobResult,
                new JobResultPayload
                {
                    JobId = completion.JobId,
                    Outcome = completion.Outcome,
                    ExitCode = completion.ExitCode,
                    Error = completion.Error,
                    FinishedAt = now,
                    DurationMs = completion.DurationMs,
                },
                now));
        }

        // A freed slot is worth a claim straight away rather than at the next tick.
        _nextClaimAt = now;
        if (CanClaim(now))
        {
            send.Add(Claim(now));
        }

        return Step(now, send, notes: [new SessionNote(
            completion.Outcome == JobOutcomes.Succeeded ? LogLevel.Info : LogLevel.Warning,
            $"job {completion.JobId}: {completion.Outcome}" +
            (completion.ExitCode is null ? string.Empty : $" (exit {completion.ExitCode})"))]);
    }

    /// <summary>A fresh probe finished (restart, daily tick, or on request from the plane).</summary>
    public SessionStep CapabilitiesRefreshed(CapabilitySet capabilities, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        _reprobeInFlight = false;
        _nextReprobeAt = now + _options.ReprobeInterval;

        var previous = _capabilities;
        _capabilities = capabilities;

        var notes = new List<SessionNote>();
        var send = new List<Envelope>();

        if (string.Equals(previous.Hash, capabilities.Hash, StringComparison.Ordinal))
        {
            notes.Add(new SessionNote(LogLevel.Debug, "capabilities unchanged"));
            return Step(now, notes: notes);
        }

        var added = capabilities.Capabilities.Except(previous.Capabilities, StringComparer.Ordinal).ToArray();
        var removed = previous.Capabilities.Except(capabilities.Capabilities, StringComparer.Ordinal).ToArray();
        if (added.Length > 0)
        {
            notes.Add(new SessionNote(LogLevel.Info, "capabilities gained: " + string.Join(", ", added)));
        }

        if (removed.Length > 0)
        {
            notes.Add(new SessionNote(LogLevel.Warning, "capabilities lost: " + string.Join(", ", removed)));
        }

        send.Add(Envelope.Create(
            MessageTypes.CapabilitiesReport,
            new CapabilitiesReportPayload
            {
                Capabilities = capabilities.Capabilities,
                ProbedAt = capabilities.ProbedAt,
                CapabilitiesHash = capabilities.Hash,
                Probes = capabilities.Reports,
            },
            now));

        return Step(now, send, notes: notes);
    }

    /// <summary>Shutting down: hand back every lease so the queue does not wait out the TTL.</summary>
    public SessionStep Drain(string reason, DateTimeOffset now)
    {
        var released = _leases.TakeAll();
        var stop = released
            .Select(jobId => new JobStop(jobId, reason, Report: false))
            .ToArray();

        _running.Clear();
        Phase = SessionPhase.Closed;

        var goodbye = Envelope.Create(
            MessageTypes.Goodbye,
            new GoodbyePayload { Reason = reason, ReleasedJobIds = released },
            now);

        return Step(now, send: [goodbye], stop: stop, close: new SessionClose(reason));
    }

    /// <summary>The connection dropped. Leases survive: the plane holds them until the TTL lapses.</summary>
    public void Disconnected()
    {
        Phase = SessionPhase.Handshaking;
        _outstandingClaimId = null;
        _outstandingHeartbeatId = null;
        _outstandingHeartbeatJobIds = [];
    }

    private SessionStep OnWelcome(Envelope envelope, DateTimeOffset now)
    {
        var welcome = envelope.ReadPayload<WelcomePayload>();
        if (welcome is null)
        {
            return Step(now, close: new SessionClose("the control plane sent an empty welcome"));
        }

        var negotiation = ProtocolNegotiation.Evaluate(welcome);
        AgentId = welcome.AgentId;

        HeartbeatInterval = Clamp(welcome.HeartbeatSeconds, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        LeaseTtl = Clamp(welcome.LeaseSeconds, TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
        ClaimInterval = Clamp(welcome.ClaimIntervalSeconds, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        _nextHeartbeatAt = now + HeartbeatInterval;
        _lastAckAt = now;

        var notes = new List<SessionNote>();
        if (!negotiation.Ok)
        {
            Phase = SessionPhase.Refusing;
            RefusalReason = negotiation.Message;
            notes.Add(new SessionNote(LogLevel.Error, negotiation.Message!));
            return Step(now, notes: notes);
        }

        Phase = SessionPhase.Ready;
        RefusalReason = null;
        notes.Add(new SessionNote(
            LogLevel.Info,
            $"registered as {welcome.AgentId} on protocol {negotiation.AgreedVersion}; " +
            string.Create(
                CultureInfo.InvariantCulture,
                $"heartbeat {HeartbeatInterval.TotalSeconds:0}s, lease {LeaseTtl.TotalSeconds:0}s, ") +
            $"{_capabilities.Capabilities.Count} capabilities advertised"));

        if (welcome.Update is { } update && !string.Equals(update.LatestVersion, AgentBuild.Version, StringComparison.Ordinal))
        {
            notes.Add(new SessionNote(
                _options.AutoUpdate ? LogLevel.Info : LogLevel.Warning,
                _options.AutoUpdate
                    ? $"charter-agent {update.LatestVersion} is available and --auto-update is set; " +
                      "the agent will install it at the next idle window"
                    : $"charter-agent {update.LatestVersion} is available (running {AgentBuild.Version}). " +
                      "Auto-update is off by default; upgrade deliberately, or pass --auto-update." +
                      (update.ReleaseNotesUrl is null ? string.Empty : $" Notes: {update.ReleaseNotesUrl}")));
        }

        var send = new List<Envelope>();
        _nextClaimAt = now;
        if (CanClaim(now))
        {
            send.Add(Claim(now));
        }

        return Step(now, send, notes: notes);
    }

    private SessionStep OnProtocolMismatch(Envelope envelope, DateTimeOffset now)
    {
        var payload = envelope.ReadPayload<ProtocolMismatchPayload>();
        var negotiation = payload is null
            ? new ProtocolNegotiation(false, 0, "The control plane refused this agent's protocol version.")
            : ProtocolNegotiation.Evaluate(payload);

        Phase = SessionPhase.Refusing;
        RefusalReason = negotiation.Message;

        return Step(
            now,
            notes: [new SessionNote(LogLevel.Error, negotiation.Message!)],
            close: new SessionClose(negotiation.Message!, Fatal: false));
    }

    /// <summary>
    /// The ack. Renewals extend a lease; <em>omissions</em> end one (sections 33.3, 33.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire contract says a job absent from the ack has lost its lease, and the control plane
    /// relies on it: <c>AgentConnection.RenewAsync</c> leaves out anything whose claim is gone — a
    /// lapsed lease the sweep re-queued, or a job an admin cancelled — precisely so the agent stops.
    /// Reading only the renewals turned that into a signal nothing acted on, and the local work went
    /// on running until its own TTL happened to expire. In that window the job is claimable by another
    /// worker, so two runners are on one session and both will try to push the same branch.
    /// </para>
    /// <para>
    /// Only the jobs this agent actually reported in the heartbeat being answered are considered. A
    /// job granted in between the heartbeat and its ack was never up for renewal, and stopping it
    /// would kill work the plane had just handed over.
    /// </para>
    /// </remarks>
    private SessionStep OnHeartbeatAck(Envelope envelope, DateTimeOffset now)
    {
        var ack = envelope.ReadPayload<HeartbeatAckPayload>();
        _lastAckAt = now;

        var notes = new List<SessionNote>();
        var stop = new List<JobStop>();
        var reprobe = false;

        if (ack is not null)
        {
            foreach (var grant in ack.Leases)
            {
                if (!_leases.Renew(grant.JobId, grant.LeaseExpiresAt))
                {
                    notes.Add(new SessionNote(
                        LogLevel.Debug,
                        $"ignoring lease renewal for job {grant.JobId}, which this agent does not hold"));
                }
            }

            foreach (var jobId in Unrenewed(envelope, ack))
            {
                _running.Remove(jobId);
                _leases.Release(jobId);

                // Report: false, for the same reason a lapsed lease reports nothing — the claim is
                // already somebody else's, and a result from here would contradict whoever holds it.
                stop.Add(new JobStop(jobId, "the control plane did not renew the lease", Report: false));
                notes.Add(new SessionNote(
                    LogLevel.Warning,
                    $"job {jobId}: the control plane did not renew the lease, stopping local work"));
            }

            if (ack.ReprobeRequested && !_reprobeInFlight)
            {
                _reprobeInFlight = true;
                reprobe = true;
                notes.Add(new SessionNote(LogLevel.Info, "the control plane asked for a capability re-probe"));
            }
        }

        return Step(now, stop: stop, notes: notes, reprobe: reprobe);
    }

    /// <summary>Jobs this agent reported in the heartbeat this ack answers, and the ack left out.</summary>
    private IReadOnlyList<string> Unrenewed(Envelope envelope, HeartbeatAckPayload ack)
    {
        // Matched by correlation where there is one. An uncorrelated ack is taken to answer the
        // outstanding heartbeat, which is what it can only be; an ack for a heartbeat two beats ago is
        // ignored rather than guessed at.
        if (_outstandingHeartbeatId is null
            || (envelope.CorrelationId is { Length: > 0 } correlation
                && !string.Equals(correlation, _outstandingHeartbeatId, StringComparison.Ordinal)))
        {
            return [];
        }

        var reported = _outstandingHeartbeatJobIds;
        _outstandingHeartbeatId = null;
        _outstandingHeartbeatJobIds = [];

        var renewed = ack.Leases.Select(grant => grant.JobId).ToHashSet(StringComparer.Ordinal);

        return [.. reported.Where(jobId => !renewed.Contains(jobId) && _leases.Holds(jobId))];
    }

    private SessionStep OnJobGrant(Envelope envelope, DateTimeOffset now)
    {
        _outstandingClaimId = null;

        var grant = envelope.ReadPayload<JobGrantPayload>();
        var send = new List<Envelope>();
        var start = new List<JobAssignment>();
        var notes = new List<SessionNote>();

        if (grant is null || grant.Jobs.Count == 0)
        {
            _nextClaimAt = now + (grant?.RetryAfterSeconds is { } seconds
                ? Clamp(seconds, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5))
                : ClaimInterval);
            return Step(now, notes: notes);
        }

        foreach (var job in grant.Jobs)
        {
            // A job this agent is already running, granted a second time. Clock skew between a lease
            // the plane thought had lapsed and one this agent still holds is enough to produce it, and
            // starting it again would put two shims on one session on one host — both pushing the same
            // branch, and only one of them reachable by a cancel. The lease is taken at its new expiry
            // and the work already in flight is left alone; it is the same job.
            if (_running.Contains(job.JobId))
            {
                _leases.Add(job.JobId, job.LeaseExpiresAt);
                notes.Add(new SessionNote(
                    LogLevel.Warning,
                    $"job {job.JobId}: granted again while already running here; keeping the running one " +
                    "and extending its lease"));
                continue;
            }

            // Refusing to claim on a version mismatch has to hold even if the plane grants anyway.
            if (Phase != SessionPhase.Ready)
            {
                send.Add(Reject(job, $"the agent is not claiming work: {RefusalReason ?? "handshake incomplete"}", now));
                notes.Add(new SessionNote(LogLevel.Warning, $"job {job.JobId}: handed back, agent is refusing work"));
                continue;
            }

            if (_running.Count >= _options.Concurrency)
            {
                send.Add(Reject(job, "the agent has no free concurrency slot", now));
                notes.Add(new SessionNote(LogLevel.Warning, $"job {job.JobId}: handed back, no free slot"));
                continue;
            }

            var missing = CapabilityMatcher.Missing(_capabilities.Capabilities, job.RequiredCapabilities);
            if (missing.Count > 0)
            {
                send.Add(Reject(job, "this host does not have: " + string.Join(", ", missing), now));
                notes.Add(new SessionNote(
                    LogLevel.Warning,
                    $"job {job.JobId}: handed back, missing {string.Join(", ", missing)}"));
                continue;
            }

            _leases.Add(job.JobId, job.LeaseExpiresAt);
            _running.Add(job.JobId);
            start.Add(job);
            notes.Add(new SessionNote(
                LogLevel.Info,
                $"job {job.JobId}: claimed ({job.Type}, attempt {job.Attempt}/{job.MaxAttempts})"));
        }

        _nextClaimAt = now + ClaimInterval;
        if (CanClaim(now))
        {
            send.Add(Claim(now));
        }

        return Step(now, send, start: start, notes: notes);
    }

    private SessionStep OnJobCancel(Envelope envelope, DateTimeOffset now)
    {
        var cancel = envelope.ReadPayload<JobCancelPayload>();
        if (cancel is null || !_leases.Holds(cancel.JobId))
        {
            return Step(now);
        }

        var reason = cancel.Reason ?? "cancelled by the control plane";
        return Step(
            now,
            stop: [new JobStop(cancel.JobId, reason, Report: true)],
            notes: [new SessionNote(LogLevel.Info, $"job {cancel.JobId}: {reason}")]);
    }

    private SessionStep OnRevoked(Envelope envelope, DateTimeOffset now)
    {
        var payload = envelope.ReadPayload<RevokedPayload>();
        var reason = payload?.Reason ?? "this agent's credential was revoked";

        var stop = _leases.TakeAll()
            .Select(jobId => new JobStop(jobId, reason, Report: false))
            .ToArray();

        _running.Clear();
        Phase = SessionPhase.Closed;

        return Step(
            now,
            stop: stop,
            notes: [new SessionNote(LogLevel.Error, $"revoked: {reason}. In-flight work stopped.")],
            close: new SessionClose(reason, CredentialRevoked: true, Fatal: true));
    }

    private SessionStep OnError(Envelope envelope, DateTimeOffset now)
    {
        var error = envelope.ReadPayload<ErrorPayload>();
        var text = error?.Message ?? error?.Code ?? "the control plane reported an error";
        return Step(now, notes: [new SessionNote(LogLevel.Warning, "control plane: " + text)]);
    }

    /// <summary>
    /// How long an unanswered claim blocks the next one.
    /// </summary>
    /// <remarks>
    /// One claim is outstanding at a time, so a slow control plane is not met with a pile of them.
    /// But a claim that is never answered - a dropped frame, a plane that restarted mid-request -
    /// must not park the agent forever, so the outstanding claim ages out and it asks again.
    /// </remarks>
    public TimeSpan ClaimTimeout => TimeSpan.FromSeconds(Math.Max(30, ClaimInterval.TotalSeconds * 4));

    /// <summary>The earliest instant a claim may next be sent, given anything already outstanding.</summary>
    private DateTimeOffset NextClaimOpportunity() =>
        _outstandingClaimId is null
            ? _nextClaimAt
            : (_outstandingClaimExpiresAt > _nextClaimAt ? _outstandingClaimExpiresAt : _nextClaimAt);

    private bool CanClaim(DateTimeOffset now) =>
        Phase == SessionPhase.Ready &&
        AvailableSlots > 0 &&
        now >= NextClaimOpportunity();

    private Envelope Claim(DateTimeOffset now)
    {
        var envelope = Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload
            {
                MaxJobs = AvailableSlots,
                Capabilities = _capabilities.Capabilities,
                Mode = _options.Mode.ToWire(),
            },
            now);

        _outstandingClaimId = envelope.Id;
        _outstandingClaimExpiresAt = now + ClaimTimeout;
        _nextClaimAt = now + ClaimInterval;
        return envelope;
    }

    private Envelope Heartbeat(DateTimeOffset now)
    {
        var held = _leases.HeldJobIds;

        var envelope = Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = Phase switch
                {
                    SessionPhase.Refusing => "refusing",
                    SessionPhase.Closed => "draining",
                    _ => _running.Count > 0 ? "busy" : "ready",
                },
                HeldJobIds = held,
                AvailableSlots = AvailableSlots,
                CapabilitiesHash = _capabilities.Hash,
            },
            now);

        // Remembered so the ack can be read for what it leaves out as well as what it carries.
        _outstandingHeartbeatId = envelope.Id;
        _outstandingHeartbeatJobIds = held;

        return envelope;
    }

    private Envelope Reject(JobAssignment job, string reason, DateTimeOffset now) =>
        Envelope.Create(
            MessageTypes.JobResult,
            new JobResultPayload
            {
                JobId = job.JobId,
                Outcome = JobOutcomes.Abandoned,
                Error = reason,
                FinishedAt = now,
            },
            now);

    private SessionStep Step(
        DateTimeOffset now,
        IReadOnlyList<Envelope>? send = null,
        IReadOnlyList<JobAssignment>? start = null,
        IReadOnlyList<JobStop>? stop = null,
        IReadOnlyList<SessionNote>? notes = null,
        bool reprobe = false,
        SessionClose? close = null) =>
        new(
            send ?? [],
            start ?? [],
            stop ?? [],
            notes ?? [],
            reprobe,
            close,
            NextWake(now));

    private DateTimeOffset NextWake(DateTimeOffset now)
    {
        var next = _nextHeartbeatAt;

        if (Phase == SessionPhase.Ready && AvailableSlots > 0 && NextClaimOpportunity() < next)
        {
            next = NextClaimOpportunity();
        }

        if (Phase == SessionPhase.Ready && !_reprobeInFlight && _nextReprobeAt < next)
        {
            next = _nextReprobeAt;
        }

        if (_leases.EarliestExpiry is { } expiry && expiry < next)
        {
            next = expiry;
        }

        return next < now ? now : next;
    }

    private static TimeSpan Clamp(int seconds, TimeSpan minimum, TimeSpan maximum) =>
        seconds <= 0 ? minimum : TimeSpan.FromSeconds(Math.Clamp(seconds, minimum.TotalSeconds, maximum.TotalSeconds));

    /// <summary>How long since the control plane last acknowledged a heartbeat.</summary>
    public TimeSpan SinceLastAck(DateTimeOffset now) => now - _lastAckAt;
}
