using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Runners.Agent;

/// <summary>
/// One live agent connection: the control plane's half of the section 33.6 frame exchange.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here pushes work.</strong> Section 33.4 is the load-bearing constraint of the
/// whole design: the agent claims and the plane answers, which is what allows the agent to sit behind
/// NAT with no inbound port. The only frames the plane originates are the ones that stop things —
/// <c>job.cancel</c> and <c>revoked</c> — and those travel on a socket the agent opened.
/// </para>
/// <para>
/// No orchestration state lives in this object beyond the set of job ids it has granted, and even
/// that is a convenience: the authority for every lease is the <c>jobs</c> row, so a control plane
/// that restarts mid-connection loses nothing an agent's next <c>hello</c> does not restore
/// (section 2.3). Every handler therefore opens its own scope and reads Postgres rather than
/// remembering.
/// </para>
/// </remarks>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly IAgentChannel _channel;
    private readonly IServiceScopeFactory _scopes;
    private readonly AgentPlaneOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<Guid, byte> _granted = new();

    private int _closeCode = 1000;
    private string _closeReason = "The control plane closed this connection.";

    public AgentConnection(
        Guid agentId,
        IAgentChannel channel,
        IServiceScopeFactory scopes,
        AgentPlaneOptions options,
        TimeProvider clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        AgentId = agentId;
        _channel = channel;
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public Guid AgentId { get; }

    /// <summary>The identity this connection claims jobs under. Also what <c>claimed_by</c> holds.</summary>
    public string WorkerId => AgentRunner.WorkerIdFor(AgentId);

    /// <summary>True once a protocol version was agreed. Nothing is granted before it.</summary>
    public bool Ready { get; private set; }

    /// <summary>The version in use on this connection, or 0 while refusing (section 33.6).</summary>
    public int AgreedProtocolVersion { get; private set; }

    /// <summary>Jobs granted on this connection and not yet reported. Diagnostics, not authority.</summary>
    public IReadOnlyCollection<Guid> GrantedJobIds => [.. _granted.Keys];

    /// <summary>
    /// Runs the connection until the agent goes away, the plane closes it, or the host shuts down.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        var token = linked.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var envelope = await ReceiveWithIdleTimeoutAsync(token);
                if (envelope is null)
                {
                    _closeReason = "The agent closed the connection.";
                    break;
                }

                await HandleAsync(envelope, token);
            }
        }
        catch (AgentChannelClosedException exception)
        {
            _logger.LogInformation(
                "Agent {AgentId} disconnected: {Reason}",
                AgentId,
                exception.Message);
        }
        catch (OperationCanceledException)
        {
            // Either the host is stopping or the plane closed this connection deliberately. Both are
            // ordinary; the close frame, if there is one to send, has already been queued.
        }
        finally
        {
            await CloseChannelAsync();
            await MarkDisconnectedAsync();
        }
    }

    /// <summary>
    /// Stops one job the agent holds (section 11: the cancel button must reach the runner).
    /// </summary>
    public async Task<bool> CancelJobAsync(Guid jobId, string reason, CancellationToken cancellationToken = default)
    {
        if (!Ready)
        {
            return false;
        }

        try
        {
            await _channel.SendAsync(
                Envelope.Create(
                    MessageTypes.JobCancel,
                    new JobCancelPayload { JobId = jobId.ToString("D"), Reason = reason },
                    _clock.GetUtcNow()),
                cancellationToken);

            return true;
        }
        catch (AgentChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Section 33.3: revocation kills in-flight jobs and invalidates the credential, immediately.
    /// </summary>
    /// <remarks>
    /// The frame goes first and the socket closes second, because the daemon's own handler stops every
    /// running job the instant it reads <c>revoked</c> — a close on its own would only make it
    /// reconnect. Returning the claims to the queue is the caller's job and happens whether or not the
    /// agent is currently connected, which is what makes revocation work on an agent that is switched
    /// off.
    /// </remarks>
    public async Task RevokeAsync(string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await _channel.SendAsync(
                Envelope.Create(MessageTypes.Revoked, new RevokedPayload { Reason = reason }, _clock.GetUtcNow()),
                cancellationToken);
        }
        catch (AgentChannelClosedException)
        {
            // Already gone. The credential is invalidated in Postgres regardless, so a reconnect
            // fails authentication rather than resuming.
        }

        RequestClose(AgentProtocol.CloseCredentialRevoked, reason);
    }

    /// <summary>Ends the connection because another socket for the same agent took over.</summary>
    public void Replace() =>
        RequestClose(AgentProtocol.CloseReplaced, "Another connection for this agent took over.");

    /// <summary>Asks the loop to stop and records the close code the agent will see.</summary>
    public void RequestClose(int closeCode, string reason)
    {
        _closeCode = closeCode;
        _closeReason = reason;

        if (!_closing.IsCancellationRequested)
        {
            _closing.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _closing.Dispose();
        await _channel.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // Frame handling.
    // ---------------------------------------------------------------------------------------------

    private async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Hello:
                await OnHelloAsync(envelope, cancellationToken);
                break;

            case MessageTypes.Heartbeat:
                await OnHeartbeatAsync(envelope, cancellationToken);
                break;

            case MessageTypes.CapabilitiesReport:
                await OnCapabilitiesReportAsync(envelope, cancellationToken);
                break;

            case MessageTypes.JobClaim:
                await OnJobClaimAsync(envelope, cancellationToken);
                break;

            case MessageTypes.JobEvent:
                await OnJobEventAsync(envelope, cancellationToken);
                break;

            case MessageTypes.JobResult:
                await OnJobResultAsync(envelope, cancellationToken);
                break;

            case MessageTypes.Goodbye:
                await OnGoodbyeAsync(envelope, cancellationToken);
                break;

            default:
                // An agent one version ahead may send something this build has no record for. Logging
                // and ignoring is right; dropping the socket over an unknown frame is not.
                _logger.LogDebug(
                    "Ignoring unknown frame type '{Type}' from agent {AgentId}",
                    envelope.Type,
                    AgentId);
                break;
        }
    }

    /// <summary>
    /// The handshake. Section 33.6: a mismatch is loud, now, and names both versions.
    /// </summary>
    private async Task OnHelloAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var hello = envelope.ReadPayload<HelloPayload>();
        if (hello is null)
        {
            await SendErrorAsync(AgentErrorCodes.MalformedFrame, "The hello frame carried no payload.", envelope, cancellationToken);
            return;
        }

        var agreed = AgentProtocol.Negotiate(hello.ProtocolVersion, hello.SupportedProtocolVersions);

        if (agreed == 0)
        {
            Ready = false;
            AgreedProtocolVersion = 0;

            var message = AgentProtocol.DescribeMismatch(hello.ProtocolVersion);

            _logger.LogWarning("Agent {AgentId} refused: {Message}", AgentId, message);

            await _channel.SendAsync(
                Envelope.Create(
                    MessageTypes.ProtocolMismatch,
                    new ProtocolMismatchPayload
                    {
                        ServerProtocolVersion = AgentProtocol.Version,
                        SupportedProtocolVersions = AgentProtocol.SupportedVersions,
                        Message = message,
                    },
                    _clock.GetUtcNow(),
                    envelope.Id),
                cancellationToken);

            RequestClose(AgentProtocol.CloseProtocolMismatch, "Protocol version mismatch.");
            return;
        }

        var now = _clock.GetUtcNow();
        var capabilities = RunnerCapability.ExpandAll(hello.Capabilities);

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
            var agent = await db.RunnerAgents.FirstOrDefaultAsync(candidate => candidate.Id == AgentId, cancellationToken);

            if (agent is null || agent.IsRevoked)
            {
                RequestClose(AgentProtocol.CloseCredentialRevoked, "This agent's credential was revoked.");
                return;
            }

            agent.Connected(
                hello.Name,
                ParseMode(hello.Mode),
                hello.AgentVersion,
                agreed,
                hello.Concurrency,
                new RunnerAgentPlatform(
                    hello.Platform.Os,
                    hello.Platform.Arch,
                    hello.Platform.Rid,
                    hello.Platform.Hostname,
                    hello.Platform.CpuCount,
                    hello.Platform.TotalMemoryMb),
                capabilities,
                hello.CapabilitiesProbedAt,
                now);

            await db.SaveChangesAsync(cancellationToken);
        }

        Ready = true;
        AgreedProtocolVersion = agreed;

        // Leases survive a reconnect (section 33.4). Renewing what the agent says it still holds is
        // what keeps a dropped socket from costing a build that is halfway through.
        await RenewAsync(hello.HeldJobIds, cancellationToken);

        await _channel.SendAsync(
            Envelope.Create(
                MessageTypes.Welcome,
                new WelcomePayload
                {
                    AgentId = AgentId.ToString("D"),
                    ProtocolVersion = agreed,
                    SupportedProtocolVersions = AgentProtocol.SupportedVersions,
                    ServerVersion = Diagnostics.BuildInfo.Version,
                    HeartbeatSeconds = _options.HeartbeatSeconds,
                    LeaseSeconds = _options.LeaseSeconds,
                    ReprobeSeconds = _options.ReprobeSeconds,
                    ClaimIntervalSeconds = _options.ClaimIntervalSeconds,
                    Update = _options.UpdateOffer,
                },
                now,
                envelope.Id),
            cancellationToken);

        _logger.LogInformation(
            "Agent {AgentId} ({Name}) connected on protocol {Protocol} in {Mode} mode with {Count} capabilities",
            AgentId,
            hello.Name,
            agreed,
            hello.Mode,
            capabilities.Count);
    }

    /// <summary>Liveness and lease renewal in one frame (sections 33.3, 33.4).</summary>
    private async Task OnHeartbeatAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var heartbeat = envelope.ReadPayload<HeartbeatPayload>();
        if (heartbeat is null)
        {
            await SendErrorAsync(AgentErrorCodes.MalformedFrame, "The heartbeat frame carried no payload.", envelope, cancellationToken);
            return;
        }

        var now = _clock.GetUtcNow();
        var reprobe = false;

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
            var agent = await db.RunnerAgents.FirstOrDefaultAsync(candidate => candidate.Id == AgentId, cancellationToken);

            if (agent is null || agent.IsRevoked)
            {
                RequestClose(AgentProtocol.CloseCredentialRevoked, "This agent's credential was revoked.");
                return;
            }

            // Section 32.2: the hash is how drift is spotted without shipping the whole set every
            // thirty seconds. A first hash is recorded silently; a *changed* one asks for a probe.
            if (agent.CapabilitiesHash is { Length: > 0 } known
                && !string.Equals(known, heartbeat.CapabilitiesHash, StringComparison.Ordinal))
            {
                reprobe = true;
            }

            agent.Heartbeat(now, heartbeat.CapabilitiesHash);
            await db.SaveChangesAsync(cancellationToken);
        }

        var leases = await RenewAsync(heartbeat.HeldJobIds, cancellationToken);

        await _channel.SendAsync(
            Envelope.Create(
                MessageTypes.HeartbeatAck,
                new HeartbeatAckPayload { Leases = leases, ReprobeRequested = reprobe },
                now,
                envelope.Id),
            cancellationToken);
    }

    private async Task OnCapabilitiesReportAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var report = envelope.ReadPayload<CapabilitiesReportPayload>();
        if (report is null)
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
        var agent = await db.RunnerAgents.FirstOrDefaultAsync(candidate => candidate.Id == AgentId, cancellationToken);

        if (agent is null || agent.IsRevoked)
        {
            return;
        }

        agent.ReportCapabilities(
            RunnerCapability.ExpandAll(report.Capabilities),
            report.ProbedAt,
            report.CapabilitiesHash);

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Agent {AgentId} re-probed and now advertises {Count} capabilities",
            AgentId,
            report.Capabilities.Count);
    }

    /// <summary>
    /// The claim. Section 33.4: the agent asks, filtered by capability, under a lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter is applied by Postgres, not here — <c>required_capabilities &lt;@ @capabilities</c>
    /// inside the <c>SKIP LOCKED</c> claim — so a job this agent cannot run is never locked, never
    /// returned, and never has to be handed back. That is the difference between "an agent only ever
    /// sees jobs it can actually run" being a property of the queue and it being a check somebody has
    /// to remember to write.
    /// </para>
    /// <para>
    /// Containment alone is not enough, because it is a test of capability and section 2.2 is a
    /// boundary. The empty set is contained in every capability set, so <c>refine</c>, <c>recap</c>
    /// and <c>update_check</c> — control plane work, enqueued requiring nothing — would be claimable
    /// by any daemon that happened to be online, and then failed by it, because their payloads were
    /// never written for the execution plane. Refinement is the front door of the product; a request
    /// whose refine job was swallowed by a Mac mini stalls with nothing on screen to explain it. So
    /// the claim also <em>requires</em> the routing marker: not "I have this capability" but "this row
    /// asked for the execution plane". Control plane rows never name it, so they are invisible here
    /// however capable the daemon is.
    /// </para>
    /// </remarks>
    private async Task OnJobClaimAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var claim = envelope.ReadPayload<JobClaimPayload>();
        if (claim is null)
        {
            await SendErrorAsync(AgentErrorCodes.MalformedFrame, "The claim frame carried no payload.", envelope, cancellationToken);
            return;
        }

        if (!Ready)
        {
            await SendErrorAsync(
                AgentErrorCodes.HandshakeRequired,
                "Send hello and agree a protocol version before claiming work.",
                envelope,
                cancellationToken);
            return;
        }

        var maxJobs = Math.Clamp(claim.MaxJobs, 0, _options.MaxJobsPerClaim);
        if (maxJobs == 0)
        {
            await SendGrantAsync([], envelope, cancellationToken);
            return;
        }

        var now = _clock.GetUtcNow();

        // What the agent says it has, expanded so matching is set containment, plus the marker that
        // makes a row agent-claimable at all. Without the marker in the set the containment test would
        // reject every agent row; without the marker demanded of the row, the same test would accept
        // every control plane row.
        var capabilities = new List<string>(RunnerCapability.ExpandAll(claim.Capabilities))
        {
            AgentRunner.ClaimCapability,
        };

        await using var scope = _scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var queue = provider.GetRequiredService<JobQueue>();

        var claimed = await queue.ClaimAsync(
            WorkerId,
            _options.Lease,
            maxJobs,
            capabilities,
            AgentRunner.ClaimCapability,
            now,
            cancellationToken);

        var assignments = new List<JobAssignment>(claimed.Count);

        foreach (var job in claimed)
        {
            var payload = AgentJobPayload.TryParse(job.Payload);
            if (payload is null)
            {
                _logger.LogError(
                    "Job {JobId} claimed by agent {AgentId} has an unreadable payload; failing it rather than granting it",
                    job.Id,
                    AgentId);

                await queue.FailAsync(
                    job.Id,
                    WorkerId,
                    "The agent job payload is not readable, so there is nothing to run.",
                    cancellationToken: cancellationToken);
                continue;
            }

            JobSecrets? secrets;
            string? eventToken;

            try
            {
                (secrets, eventToken) = await MintAsync(provider, payload, cancellationToken);
            }
            catch (SessionNotLiveException exception)
            {
                // The session this row belongs to is over, cancelled, or was never dispatched. There is
                // nothing to run and nothing to credential, so the row is settled rather than handed
                // back: returning it would re-offer the same dead work on the next claim, forever.
                _logger.LogWarning(
                    "Refusing to mint credentials for session {SessionId}: {Reason} The job is being "
                    + "completed rather than granted to agent {AgentId}.",
                    payload.SessionId,
                    exception.Message,
                    AgentId);

                await queue.CompleteAsync(job.Id, WorkerId, now, cancellationToken);
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A job that cannot be given credentials cannot be run, and running it without them
                // would burn an attempt on a failure the operator would have to read a transcript to
                // understand. Put it back untouched and say why.
                _logger.LogError(
                    exception,
                    "Could not mint per-job credentials for session {SessionId}; returning the job to the queue",
                    payload.SessionId);

                await ReturnToQueueAsync(queue, job, now, cancellationToken);
                await SendErrorAsync(
                    AgentErrorCodes.UnknownJob,
                    "The control plane could not mint credentials for this job, so it was returned to "
                    + "the queue. " + exception.Message,
                    envelope,
                    cancellationToken);
                continue;
            }

            assignments.Add(AgentJobAssignmentBuilder.Build(job, payload, secrets, eventToken, _options));
            _granted[job.Id] = 0;

            _logger.LogInformation(
                "Granted job {JobId} (session {SessionId}, attempt {Attempt}/{MaxAttempts}) to agent {AgentId} until {LeaseExpiresAt:O}",
                job.Id,
                payload.SessionId,
                job.Attempts,
                job.MaxAttempts,
                AgentId,
                job.LeaseExpiresAt);
        }

        await SendGrantAsync(assignments, envelope, cancellationToken);
    }

    /// <summary>
    /// One streamed line from a running job (section 11: the status thread is fed from here).
    /// </summary>
    /// <remarks>
    /// Gated on the handshake and on the claim. The frame names its own job id, so without the second
    /// check any authenticated daemon could append to the transcript of a session it was never given
    /// — and a transcript is what a requester and an engineer both read as the account of what
    /// happened. An event for a job this agent no longer holds is dropped rather than written: the
    /// worker that does hold it is the one whose account counts.
    /// </remarks>
    private async Task OnJobEventAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.ReadPayload<JobEventPayload>();
        if (payload is null || !Ready || !Guid.TryParse(payload.JobId, out var jobId))
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        if (await ResolveHeldSessionAsync(provider, jobId, cancellationToken) is not { } sessionId)
        {
            return;
        }

        var journal = provider.GetRequiredService<SessionJournal>();

        // Keyed on the agent's own monotonic sequence, so a reconnect that replays the tail of a
        // stream is a no-op rather than a second copy of every line (section 2.3).
        await journal.AppendAsync(
            sessionId,
            EventTypes.Command,
            new JsonObject
            {
                ["source"] = "charter-agent",
                ["kind"] = payload.Kind,
                ["message"] = payload.Message,
                ["job_id"] = payload.JobId,
            }.ToJsonString(),
            $"agent-event:{payload.JobId}:{payload.Sequence.ToString(CultureInfo.InvariantCulture)}",
            payload.At,
            cancellationToken);
    }

    /// <summary>The terminal report. The lease is released here, on receipt (section 33.4).</summary>
    private async Task OnJobResultAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var result = envelope.ReadPayload<JobResultPayload>();
        if (result is null || !Guid.TryParse(result.JobId, out var jobId))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        _granted.TryRemove(jobId, out _);

        await using var scope = _scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var queue = provider.GetRequiredService<JobQueue>();

        switch (result.Outcome)
        {
            case AgentJobOutcomes.Succeeded:
            case AgentJobOutcomes.Cancelled:
                await queue.CompleteAsync(jobId, WorkerId, now, cancellationToken);
                break;

            case AgentJobOutcomes.Abandoned:
                // The agent handed the work back rather than failing it — a lapsed lease, a slot it
                // no longer had, a capability it no longer has. Returning it without burning an
                // attempt is the difference between "try another runner" and "fail after three".
                await ReturnToQueueAsync(queue, jobId, result.Error, now, cancellationToken);
                break;

            default:
                await queue.FailAsync(
                    jobId,
                    WorkerId,
                    result.Error ?? $"The agent reported the job as {result.Outcome}.",
                    TimeSpan.FromSeconds(30),
                    now,
                    cancellationToken);
                break;
        }

        _logger.LogInformation(
            "Agent {AgentId} reported job {JobId} as {Outcome}{ExitCode}",
            AgentId,
            jobId,
            result.Outcome,
            result.ExitCode is null ? string.Empty : $" (exit {result.ExitCode})");
    }

    /// <summary>The agent is shutting down cleanly and is handing its leases back (section 31).</summary>
    private async Task OnGoodbyeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var goodbye = envelope.ReadPayload<GoodbyePayload>();

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
            var released = await queue.ReleaseWorkerClaimsAsync(WorkerId, _clock.GetUtcNow(), cancellationToken);

            if (released > 0)
            {
                _logger.LogInformation(
                    "Agent {AgentId} said goodbye and returned {Count} job(s) to the queue",
                    AgentId,
                    released);
            }
        }

        _granted.Clear();
        RequestClose(1000, goodbye?.Reason ?? "The agent is shutting down.");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mints the two credentials of section 33.5, at claim time and never before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what crosses to an agent host: a short-TTL token for the one repository
    /// in this job, and a model credential. Never a refresh token — the control plane owns OAuth
    /// refresh (section 20b.2) — never this process's environment, and never a token for a repository
    /// other than the one the <em>session</em> belongs to.
    /// </para>
    /// <para>
    /// Gated on the session being live, exactly as the HTTP exchange is. A job row outliving its
    /// session is ordinary rather than exotic: cancelling a claimed session fails its job, and a
    /// failed job with an attempt left goes back to <c>Pending</c> for the next agent to claim. Minting
    /// there would hand a contribute-scoped GitHub token and a twelve-hour event token to a runner for
    /// work that had already been called off — a credential outliving the thing that justified it
    /// (sections 6, 16, 33.5).
    /// </para>
    /// <para>
    /// The repository is read from the session rather than taken from the payload, so the token
    /// follows the session's own aggregate and a row whose payload disagrees is refused instead of
    /// being trusted.
    /// </para>
    /// </remarks>
    /// <exception cref="SessionNotLiveException">The session may not be given credentials.</exception>
    private async Task<(JobSecrets? Secrets, string? EventToken)> MintAsync(
        IServiceProvider provider,
        AgentJobPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.RepoFullName is not { Length: > 0 } repo)
        {
            return (null, null);
        }

        var decision = await SessionCredentialGuard.EvaluateAsync(
            provider.GetRequiredService<CharterDbContext>(),
            provider.GetRequiredService<SessionJournal>(),
            payload.SessionId,
            cancellationToken);

        if (!decision.Allowed)
        {
            throw new SessionNotLiveException(decision);
        }

        if (!string.Equals(decision.RepoFullName, repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionNotLiveException(
                new SessionCredentialDecision(SessionCredentialRefusal.UnknownSession, decision.RepoFullName),
                "This job names a repository its session does not belong to, so no credentials will be "
                + "issued for it.");
        }

        var broker = provider.GetRequiredService<IRunnerCredentialBroker>();
        var credentials = await broker.IssueAsync(payload.SessionId, repo, cancellationToken);

        var model = credentials.ModelApiKey is { Length: > 0 } key
            ? new ModelCredential
            {
                Provider = ModelIdentifier.TryParse(payload.Model, out var identifier)
                    ? ModelIdentifier.ProviderPrefix(identifier.Provider)
                    : "anthropic",
                ApiKey = key,
            }
            : null;

        var secrets = new JobSecrets
        {
            GitHub = new GitHubInstallationToken
            {
                Token = credentials.GitHubToken,
                Repository = repo,
                ExpiresAt = _clock.GetUtcNow() + _options.RepositoryTokenLifetime,
            },
            Model = model,
        };

        var tokens = provider.GetService<RunnerSessionTokens>();
        return (secrets, tokens?.IssueEventToken(payload.SessionId, _clock.GetUtcNow()));
    }

    /// <summary>Renews every lease the agent still holds, and reports which survived.</summary>
    private async Task<IReadOnlyList<LeaseGrant>> RenewAsync(
        IReadOnlyList<string> heldJobIds,
        CancellationToken cancellationToken)
    {
        if (heldJobIds.Count == 0)
        {
            return [];
        }

        var now = _clock.GetUtcNow();
        var expiresAt = now + _options.Lease;
        var grants = new List<LeaseGrant>(heldJobIds.Count);

        await using var scope = _scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        foreach (var id in heldJobIds)
        {
            if (!Guid.TryParse(id, out var jobId))
            {
                continue;
            }

            // False means the claim is gone: the lease lapsed and the sweep re-queued it, or an
            // admin cancelled it. Omitting it from the ack is how the agent is told to stop.
            if (await queue.RenewLeaseAsync(jobId, WorkerId, _options.Lease, now, cancellationToken))
            {
                grants.Add(new LeaseGrant { JobId = jobId.ToString("D"), LeaseExpiresAt = expiresAt });
                _granted[jobId] = 0;
            }
            else
            {
                _granted.TryRemove(jobId, out _);
                _logger.LogWarning(
                    "Agent {AgentId} still holds job {JobId} but the claim is gone; it will be told to stop",
                    AgentId,
                    jobId);
            }
        }

        return grants;
    }

    /// <summary>
    /// Puts a claimed job back without burning an attempt.
    /// </summary>
    /// <remarks>
    /// The same manoeuvre <c>QueueDispatcher.DeferAsync</c> makes, and for the same reason: the queue
    /// has no "later" verb, so expressing one means enqueueing the equivalent row and completing the
    /// claim. The new row goes in first — a crash between the two leaves a duplicate, which the
    /// dispatch claim in the session journal makes harmless, whereas the other order loses the work.
    /// </remarks>
    private async Task ReturnToQueueAsync(
        JobQueue queue,
        ClaimedJob job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await queue.EnqueueAsync(
            job.Type,
            job.Payload,
            maxAttempts: job.MaxAttempts,
            requiredCapabilities: job.RequiredCapabilities,
            availableAt: now + _options.ClaimInterval + _options.ClaimInterval,
            now: now,
            cancellationToken: cancellationToken);

        await queue.CompleteAsync(job.Id, WorkerId, now, cancellationToken);
    }

    /// <summary>
    /// The same manoeuvre for a job named by a frame rather than held in hand.
    /// </summary>
    /// <remarks>
    /// The claim is re-read and checked before anything is written. A <c>job.result</c> frame carries
    /// a job id the agent chose, and the row is the only authority on who holds it — so re-enqueueing
    /// on the frame's word alone would let a stale reconnect, or a daemon reporting on a lease that
    /// lapsed under it, mint a second copy of work another worker is already running. The
    /// <c>CompleteAsync</c> below is guarded by <c>claimed_by</c> and would no-op in that case, which
    /// is precisely what leaves the duplicate behind with nothing to cancel it.
    /// </remarks>
    private async Task ReturnToQueueAsync(
        JobQueue queue,
        Guid jobId,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var row = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);
        if (row is null)
        {
            return;
        }

        if (row.Status != Domain.JobStatus.Claimed
            || !string.Equals(row.ClaimedBy, WorkerId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Agent {AgentId} abandoned job {JobId}, but the claim is held by {ClaimedBy}; "
                + "leaving the queue alone",
                AgentId,
                jobId,
                row.ClaimedBy ?? "nobody");

            return;
        }

        _logger.LogInformation(
            "Agent {AgentId} abandoned job {JobId}; returning it to the queue. {Reason}",
            AgentId,
            jobId,
            reason);

        await queue.EnqueueAsync(
            row.Type,
            row.Payload,
            maxAttempts: row.MaxAttempts,
            requiredCapabilities: row.RequiredCapabilities,
            availableAt: now + _options.ClaimInterval + _options.ClaimInterval,
            now: now,
            cancellationToken: cancellationToken);

        await queue.CompleteAsync(jobId, WorkerId, now, cancellationToken);
    }

    /// <summary>
    /// The session a job belongs to, but only while <em>this</em> agent holds the claim.
    /// </summary>
    /// <remarks>
    /// The job id arrives in a frame the agent composed, so it is a request rather than a fact. The
    /// row is the authority on who holds a claim (section 2.3), and restricting the lookup to this
    /// worker's claims is what keeps one connected daemon from writing into the transcript of a
    /// session belonging to somebody else's repository — a stream the requester and the engineer both
    /// read as an account of what happened (sections 11, 16).
    /// </remarks>
    private async Task<Guid?> ResolveHeldSessionAsync(
        IServiceProvider provider,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<CharterDbContext>();

        var payload = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId
                          && job.Status == Domain.JobStatus.Claimed
                          && job.ClaimedBy == WorkerId)
            .Select(job => job.Payload)
            .FirstOrDefaultAsync(cancellationToken);

        return payload is null ? null : AgentJobPayload.TryParse(payload)?.SessionId;
    }

    private Task SendGrantAsync(
        IReadOnlyList<JobAssignment> assignments,
        Envelope claim,
        CancellationToken cancellationToken) =>
        _channel.SendAsync(
            Envelope.Create(
                MessageTypes.JobGrant,
                new JobGrantPayload
                {
                    Jobs = assignments,
                    RetryAfterSeconds = assignments.Count == 0 ? _options.EmptyClaimRetryAfterSeconds : null,
                },
                _clock.GetUtcNow(),
                claim.Id),
            cancellationToken);

    private Task SendErrorAsync(string code, string message, Envelope? cause, CancellationToken cancellationToken) =>
        _channel.SendAsync(
            Envelope.Create(
                MessageTypes.Error,
                new ErrorPayload { Code = code, Message = message },
                _clock.GetUtcNow(),
                cause?.Id),
            cancellationToken);

    /// <summary>
    /// Reads the next frame, giving up on a connection that has gone silent.
    /// </summary>
    /// <remarks>
    /// The agent owes a heartbeat on an interval the plane set in <c>welcome</c>. Several missed ones
    /// mean the socket is a half-open TCP connection nobody has noticed, and holding it costs a slot
    /// forever. Closing is cheap for a healthy agent: it reconnects and reports what it still holds.
    /// </remarks>
    private async Task<Envelope?> ReceiveWithIdleTimeoutAsync(CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Ready ? _options.IdleTimeout : _options.HandshakeTimeout);

        try
        {
            return await _channel.ReceiveAsync(idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _closeReason = Ready
                ? "The agent stopped heartbeating."
                : "The agent connected but never sent hello.";

            _logger.LogWarning("Closing agent {AgentId}: {Reason}", AgentId, _closeReason);
            return null;
        }
    }

    private async Task CloseChannelAsync()
    {
        try
        {
            await _channel.CloseAsync(_closeCode, _closeReason, CancellationToken.None);
        }
        catch (AgentChannelClosedException)
        {
            // Nothing to close.
        }
    }

    private async Task MarkDisconnectedAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
            var agent = await db.RunnerAgents.FirstOrDefaultAsync(candidate => candidate.Id == AgentId);

            if (agent is null)
            {
                return;
            }

            agent.Disconnected();
            await db.SaveChangesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A control plane that cannot write "offline" is not a reason to fail a shutdown: the
            // heartbeat window in RunnerAgent.IsOnlineAt covers exactly this case.
            _logger.LogWarning(exception, "Could not mark agent {AgentId} offline", AgentId);
        }
    }

    internal static RunnerAgentMode ParseMode(string? mode) =>
        string.Equals(mode?.Trim(), "native", StringComparison.OrdinalIgnoreCase)
            ? RunnerAgentMode.Native
            : RunnerAgentMode.Docker;
}
