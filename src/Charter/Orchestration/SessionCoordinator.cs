using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>What happened when a queued session was handed to the execution plane.</summary>
public enum DispatchDecision
{
    /// <summary>A backend accepted it.</summary>
    Dispatched,

    /// <summary>Somebody had already dispatched it. Not an error — this is the guarantee working.</summary>
    AlreadyDispatched,

    /// <summary>The session is cancelled, terminal, or gone. Nothing to do.</summary>
    Skipped,

    /// <summary>Section 27.3: no eligible runner. The session waits, with an explanation.</summary>
    Queued,

    /// <summary>A backend was found and refused, or threw. The job retries.</summary>
    Failed,
}

/// <summary>The outcome, with the sentence a human would be shown.</summary>
public sealed record DispatchOutcome(DispatchDecision Decision, string? Explanation, RunnerKind? Runner);

/// <summary>What happened when an approved specification was turned into a session.</summary>
/// <param name="Session">The session, or null when there is not one and will not be.</param>
/// <param name="Created">False when the session already existed — a retry, not an error.</param>
/// <param name="Explanation">Why there is no session, when there is not one.</param>
/// <param name="WaitingForApproval">
/// True when the specification is unapproved and section 7.5's auto-dispatch policy does not cover
/// it. The job waits rather than failing: an approver pressing the button is what unblocks it.
/// </param>
public sealed record SessionMaterialization(
    Session? Session,
    bool Created,
    string? Explanation = null,
    bool WaitingForApproval = false);

/// <summary>
/// Everything that changes a session's state, in one place, reading and writing Postgres only.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the queue dispatcher and the session orchestrator because they need exactly the same
/// operations from different triggers — a claimed job, a recovery sweep, a cancel button, a webhook.
/// Keeping them here means there is one implementation of "dispatch this session, but never twice",
/// rather than one per caller with subtly different idempotency.
/// </para>
/// <para>
/// Nothing in this class holds state between calls. Every method starts by reading what Postgres
/// says, which is what makes the same code correct on a fresh container as on one that has been up
/// for a week (section 2.3).
/// </para>
/// </remarks>
public sealed class SessionCoordinator
{
    private readonly CharterDbContext _db;
    private readonly SessionJournal _journal;
    private readonly IRunnerRegistry _registry;
    private readonly ISessionDispatchPlanner _planner;
    private readonly JobQueue _queue;
    private readonly OrchestrationOptions _options;
    private readonly IAutoDispatchGate _autoDispatch;
    private readonly ILogger<SessionCoordinator> _logger;

    public SessionCoordinator(
        CharterDbContext db,
        SessionJournal journal,
        IRunnerRegistry registry,
        ISessionDispatchPlanner planner,
        JobQueue queue,
        OrchestrationOptions options,
        ILogger<SessionCoordinator> logger,
        IAutoDispatchGate? autoDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _journal = journal;
        _registry = registry;
        _planner = planner;
        _queue = queue;
        _options = options;
        _logger = logger;

        // Deny by default when nothing supplies a gate. An unapproved specification then waits for a
        // human, which is the safe direction and the one section 7.5 asks for when no policy applies.
        _autoDispatch = autoDispatch ?? NoAutoDispatchGate.Instance;
    }

    /// <summary>The gate a host that wires no policy resolver gets: nothing auto-dispatches.</summary>
    private sealed class NoAutoDispatchGate : IAutoDispatchGate
    {
        public static NoAutoDispatchGate Instance { get; } = new();

        public Task<AutoDispatchPermission> PermitsAsync(
            Domain.Request request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AutoDispatchPermission.Blocked(
                "no auto-dispatch policy applies, so the specification waits for an approver"));
    }

    /// <summary>Enqueues the build job for an approved session (section 6, <c>SpecReady → Queued</c>).</summary>
    public async Task<Job> EnqueueAsync(
        BuildJobPayload payload,
        IEnumerable<string>? requiredCapabilities = null,
        DateTimeOffset? availableAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return await _queue.EnqueueAsync(
            JobType.Build,
            payload.ToJson(),
            requiredCapabilities: requiredCapabilities,
            availableAt: availableAt,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Enqueues the build of an approved specification that has no session yet (section 7.5).
    /// </summary>
    /// <remarks>
    /// The same payload the API writes at the spend gate, so the auto-dispatch path and the approval
    /// path converge on one handler rather than each growing their own idea of what a build is.
    /// </remarks>
    public async Task<Job> EnqueueSpecAsync(
        SpecBuildPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return await _queue.EnqueueAsync(
            JobType.Build,
            payload.ToJson(),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Turns an approved specification into the session that will build it, at most once, ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The session is created here, by the handler, and not by the API at approval time.</strong>
    /// Section 2.3 allows the container to die between the spend gate opening and the work starting,
    /// and the two orderings fail differently. Creating the session at approval leaves two rows that
    /// have to be written in two transactions — a session, then the job that dispatches it — so a
    /// restart in the gap strands a queued session nobody is looking for, and a second mechanism has
    /// to exist to find it. Creating it here leaves one durable row, the job, which the queue already
    /// leases, reclaims and retries; the session is derived from the payload rather than remembered.
    /// </para>
    /// <para>
    /// That derivation is what makes the retry safe. <see cref="SpecBuildPayload.SessionIdFor"/> is a
    /// pure function of the specification, so a handler running twice computes the same id twice and
    /// the second insert loses a race with the first against the primary key rather than producing a
    /// second session. Nothing here is remembered between calls.
    /// </para>
    /// <para>
    /// Section 7.5 is checked, not assumed. A specification a human approved dispatches because they
    /// approved it. One that nobody approved dispatches only where auto-dispatch covers that person,
    /// that repository and that spend — and the session records
    /// <see cref="Session.AutoDispatched"/> so the change request is labelled and the recap leads
    /// with it.
    /// </para>
    /// </remarks>
    public async Task<SessionMaterialization> EnsureSessionAsync(
        SpecBuildPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var context = await (from spec in _db.Specs.AsNoTracking()
                             where spec.Id == payload.SpecId
                             join request in _db.Requests.AsNoTracking() on spec.RequestId equals request.Id
                             select new { Spec = spec, Request = request })
            .FirstOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            return new SessionMaterialization(
                null,
                false,
                "The specification this build was queued for no longer exists.");
        }

        var discriminator = payload.IsRebuild
            ? await RebuildDiscriminatorAsync(context.Request.Id, cancellationToken)
            : null;

        var sessionId = SpecBuildPayload.SessionIdFor(payload.SpecId, discriminator);

        var existing = await _db.Sessions.FirstOrDefaultAsync(
            candidate => candidate.Id == sessionId,
            cancellationToken);

        if (existing is not null)
        {
            return new SessionMaterialization(existing, Created: false);
        }

        var autoDispatched = !context.Spec.IsApproved;

        if (autoDispatched)
        {
            var permitted = await _autoDispatch.PermitsAsync(context.Request, cancellationToken);

            if (!permitted.Enabled)
            {
                return new SessionMaterialization(null, false, permitted.Reason, WaitingForApproval: true);
            }
        }

        var session = Session.Queue(
            payload.SpecId,
            _options.DefaultRunner,
            _options.BuildModel,
            autoDispatched: autoDispatched,
            id: sessionId);

        _db.Sessions.Add(session);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Another dispatcher — or this one, either side of a restart — derived the same id and
            // got there first. That is the guarantee working, not a failure.
            _db.Entry(session).State = EntityState.Detached;

            var winner = await _db.Sessions.FirstOrDefaultAsync(
                candidate => candidate.Id == sessionId,
                cancellationToken);

            return winner is null
                ? new SessionMaterialization(null, false, "The session could not be created.")
                : new SessionMaterialization(winner, Created: false);
        }

        _logger.LogInformation(
            "Specification {SpecId} became session {SessionId} ({Approval})",
            payload.SpecId,
            session.Id,
            autoDispatched ? "auto-dispatched, no human approved the spec" : "approved by a human");

        return new SessionMaterialization(session, Created: true);
    }

    /// <summary>
    /// Hands one session to a backend, at most once, ever.
    /// </summary>
    /// <remarks>
    /// The order is the whole design. The dispatch is <em>claimed in Postgres before the backend is
    /// called</em>, under a key derived from the session id and the dispatch generation, so two
    /// dispatchers — or one dispatcher either side of a container restart — race for a primary key and
    /// exactly one wins. A backend that then refuses is undone by a compensating event, which is what
    /// allows an honest retry without reopening the double-dispatch window.
    /// </remarks>
    public async Task<DispatchOutcome> DispatchAsync(
        BuildJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var summary = await _journal.SummarizeAsync(payload.SessionId, cancellationToken);

        if (summary.Dispatched)
        {
            _logger.LogInformation(
                "Session {SessionId} is already dispatched to {Runner}; not dispatching again",
                payload.SessionId,
                summary.Runner);

            return new DispatchOutcome(DispatchDecision.AlreadyDispatched, null, summary.Runner);
        }

        var dispatch = await _planner.PlanAsync(payload, cancellationToken);
        if (dispatch is null)
        {
            return new DispatchOutcome(
                DispatchDecision.Skipped,
                "The session is cancelled, terminal, or no longer exists.",
                null);
        }

        var session = await _db.Sessions.FirstOrDefaultAsync(
            candidate => candidate.Id == payload.SessionId,
            cancellationToken);

        if (session is null)
        {
            return new DispatchOutcome(DispatchDecision.Skipped, "The session no longer exists.", null);
        }

        var routing = await _registry.RouteAsync(dispatch.RequiredCapabilities, session.Runner, cancellationToken);

        if (!routing.IsRoutable)
        {
            await ExplainQueuedAsync(payload.SessionId, dispatch.RequiredCapabilities, routing, cancellationToken);
            return new DispatchOutcome(DispatchDecision.Queued, routing.Explanation, null);
        }

        var runner = routing.Runner!;
        var generation = summary.DispatchGeneration;

        // The claim. Written first, deliberately.
        var claim = await _journal.AppendAsync(
            payload.SessionId,
            OrchestrationEventTypes.SessionDispatched,
            new JsonObject
            {
                ["runner"] = SessionJournal.WireName(runner.Kind),
                ["generation"] = generation,
                ["repo"] = dispatch.RepoFullName,
                ["base_commit_sha"] = dispatch.BaseCommitSha,
                ["adapter"] = dispatch.AdapterId,
                ["model"] = dispatch.Model,
            }.ToJsonString(),
            DispatchClaimKey(payload.SessionId, generation),
            cancellationToken: cancellationToken);

        if (!claim.Appended)
        {
            // Another dispatcher, or an earlier life of this one, got there first.
            return new DispatchOutcome(DispatchDecision.AlreadyDispatched, null, runner.Kind);
        }

        try
        {
            var result = await runner.DispatchAsync(dispatch, cancellationToken);

            if (!result.Accepted)
            {
                await RecordDispatchFailureAsync(payload.SessionId, generation, result.Explanation!, cancellationToken);
                return new DispatchOutcome(DispatchDecision.Failed, result.Explanation, runner.Kind);
            }

            if (!string.IsNullOrWhiteSpace(result.ExternalReference))
            {
                await _journal.AppendAsync(
                    payload.SessionId,
                    EventTypes.SessionStarted,
                    new JsonObject { ["run_url"] = result.ExternalReference }.ToJsonString(),
                    $"dispatch-ref:{payload.SessionId:D}:{generation}",
                    cancellationToken: cancellationToken);
            }

            if (session.Status == SessionStatus.Queued)
            {
                session.Start(dispatch.BaseCommitSha);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new DispatchOutcome(DispatchDecision.Dispatched, null, runner.Kind);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Dispatching session {SessionId} failed", payload.SessionId);
            await RecordDispatchFailureAsync(payload.SessionId, generation, exception.Message, cancellationToken);
            return new DispatchOutcome(DispatchDecision.Failed, exception.Message, runner.Kind);
        }
    }

    /// <summary>
    /// Section 11: the cancel button must actually kill the runner and settle cost.
    /// </summary>
    /// <remarks>
    /// All three halves happen here and all three are idempotent, because the cancel path is exactly
    /// the path most likely to be interrupted: a user presses cancel because something is wrong, and
    /// "something is wrong" is correlated with the container going away.
    /// </remarks>
    public async Task<bool> CancelAsync(
        Guid sessionId,
        string reason = "Cancelled by request.",
        CancellationToken cancellationToken = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(
            candidate => candidate.Id == sessionId,
            cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return false;
        }

        session.RequestCancellation();
        await _db.SaveChangesAsync(cancellationToken);

        await _journal.AppendAsync(
            sessionId,
            OrchestrationEventTypes.SessionCancelRequested,
            new JsonObject { ["reason"] = reason }.ToJsonString(),
            $"cancel:{sessionId:D}",
            cancellationToken: cancellationToken);

        var summary = await _journal.SummarizeAsync(sessionId, cancellationToken);

        // 1. Kill the runner.
        if (summary.Dispatched && summary.Runner is { } kind)
        {
            var runner = _registry.Runners.FirstOrDefault(candidate => candidate.Kind == kind);

            if (runner is not null)
            {
                // The one repository this cancel may touch, read from the session's own aggregate. The
                // external reference is folded from events and session_started arrives from the
                // execution plane, so the backend needs a trustworthy value to check it against
                // (sections 7.4, 16).
                var repo = await SessionCredentialGuard.SessionRepoFullNameAsync(
                    _db,
                    sessionId,
                    cancellationToken);

                var stopped = await runner.CancelAsync(
                    new RunnerCancellation(sessionId, summary.ExternalReference, reason, repo),
                    cancellationToken);

                if (stopped.Stopped)
                {
                    _logger.LogInformation(
                        "Cancellation of session {SessionId} on {Runner}: the run was stopped",
                        sessionId,
                        kind);
                }
                else
                {
                    // Section 11 promises the cancel button kills the runner. When it did not, the
                    // session still settles here — there is nothing better to do with it — but an
                    // operator has to be able to find out that something may still be spending.
                    _logger.LogWarning(
                        "Cancellation of session {SessionId} on {Runner} stopped nothing: {Explanation}",
                        sessionId,
                        kind,
                        stopped.Explanation);
                }
            }
        }

        // 2. Take the job out of the queue if it never started.
        await CancelPendingJobsAsync(sessionId, cancellationToken);

        // 3. Settle.
        await SettleAsync(sessionId, SessionStatus.Cancelled, reason, cancellationToken);

        return true;
    }

    /// <summary>
    /// Applies a terminal outcome to the session row and settles the cost the transcript recorded.
    /// </summary>
    /// <remarks>
    /// Cost is summed from <c>cost</c> events rather than tracked in memory, so a session that ended
    /// while the control plane was restarting still settles at the right figure. Section 20b.5: a
    /// subscription-backed session legitimately settles at zero dollars and still consumed quota,
    /// which the ledger records separately.
    /// </remarks>
    public async Task<bool> SettleAsync(
        Guid sessionId,
        SessionStatus status,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(
            candidate => candidate.Id == sessionId,
            cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return false;
        }

        var summary = await _journal.SummarizeAsync(sessionId, cancellationToken);
        var outstanding = summary.ReportedCostUsd - session.CostUsd;

        if (outstanding > 0m)
        {
            session.AddCost(outstanding);
        }

        session.TransitionTo(status);
        await _db.SaveChangesAsync(cancellationToken);

        await _journal.AppendAsync(
            sessionId,
            EventTypes.SessionEnded,
            new JsonObject
            {
                ["state"] = status.ToString().ToLowerInvariant(),
                ["cost_usd"] = session.CostUsd,
                ["reason"] = reason,
            }.ToJsonString(),
            $"settled:{sessionId:D}",
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Session {SessionId} settled as {Status} at {CostUsd} USD",
            sessionId,
            status,
            session.CostUsd);

        return true;
    }

    /// <summary>
    /// Section 27.3: records why a session is waiting, without failing it.
    /// </summary>
    /// <remarks>
    /// Keyed on the explanation itself, so the same message is written once however many times the
    /// sweep runs, and a <em>changed</em> message — a runner came online, a different capability is
    /// now the blocker — is written as a new event the requester's thread can show.
    /// </remarks>
    public async Task ExplainQueuedAsync(
        Guid sessionId,
        IReadOnlyList<string> required,
        RunnerRouting routing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routing);

        var explanation = routing.Explanation ?? "No runner is available for this session yet.";

        await _journal.AppendAsync(
            sessionId,
            OrchestrationEventTypes.SessionQueued,
            new JsonObject
            {
                ["explanation"] = explanation,
                ["required"] = ToArray(required),
                ["missing"] = ToArray(routing.Missing),
            }.ToJsonString(),
            $"queued:{SessionJournal.DeterministicEventId(sessionId, explanation):N}",
            cancellationToken: cancellationToken);

        _logger.LogInformation("Session {SessionId} is queued: {Explanation}", sessionId, explanation);
    }

    /// <summary>The idempotency key that makes a dispatch single-flight.</summary>
    public static string DispatchClaimKey(Guid sessionId, int generation)
        => $"dispatch:{sessionId:D}:{generation}";

    private async Task RecordDispatchFailureAsync(
        Guid sessionId,
        int generation,
        string reason,
        CancellationToken cancellationToken)
    {
        await _journal.AppendAsync(
            sessionId,
            OrchestrationEventTypes.SessionDispatchFailed,
            new JsonObject { ["generation"] = generation, ["reason"] = reason }.ToJsonString(),
            $"dispatch-failed:{sessionId:D}:{generation}",
            cancellationToken: cancellationToken);

        await _journal.AppendAsync(
            sessionId,
            EventTypes.Error,
            new JsonObject { ["reason"] = "dispatch_failed", ["message"] = reason }.ToJsonString(),
            $"dispatch-error:{sessionId:D}:{generation}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The feedback row that asked for this rebuild, as the session id's discriminator (section 11).
    /// </summary>
    /// <remarks>
    /// "Not quite" becomes a new session on the same spec, so the session id cannot be a function of
    /// the specification alone. It is a function of the specification and the row that asked — durable,
    /// written before the job was, and therefore the same value on both sides of a restart.
    /// </remarks>
    private async Task<string?> RebuildDiscriminatorAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var feedback = await _db.RequestFeedback
            .AsNoTracking()
            .Where(row => row.RequestId == requestId && row.Verdict == FeedbackVerdict.NotQuite)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return feedback?.ToString("D");
    }

    /// <summary>
    /// The session an approval-shaped build job names, without reading anything.
    /// </summary>
    /// <remarks>
    /// A rebuild is deliberately excluded: its session is derived from a feedback row rather than
    /// from the specification alone, so it names a session that does not exist yet — and cancelling
    /// the session a requester rejected must not also cancel the rebuild they asked for instead.
    /// </remarks>
    internal static Guid? NamesSession(string payload)
        => SpecBuildPayload.TryParse(payload) is { IsRebuild: false } parsed
            ? SpecBuildPayload.SessionIdFor(parsed.SpecId)
            : null;

    private static bool IsDuplicate(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        };

    private async Task CancelPendingJobsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var pending = await _db.Jobs
            .Where(job => job.Type == JobType.Build && job.Status == JobStatus.Pending)
            .ToListAsync(cancellationToken);

        // Both payload shapes name this session: the dispatcher's own by session id, and the spend
        // gate's by specification. Cancelling only the first would leave the approval-shaped job in
        // the queue, and it would build the session a person had just cancelled.
        var affected = pending
            .Where(job => BuildJobPayload.TryParse(job.Payload)?.SessionId == sessionId
                          || NamesSession(job.Payload) == sessionId)
            .ToArray();

        foreach (var job in affected)
        {
            job.Cancel();
        }

        if (affected.Length > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
