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
    private readonly ILogger<SessionCoordinator> _logger;

    public SessionCoordinator(
        CharterDbContext db,
        SessionJournal journal,
        IRunnerRegistry registry,
        ISessionDispatchPlanner planner,
        JobQueue queue,
        OrchestrationOptions options,
        ILogger<SessionCoordinator> logger)
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
                var stopped = await runner.CancelAsync(
                    new RunnerCancellation(sessionId, summary.ExternalReference, reason),
                    cancellationToken);

                _logger.LogInformation(
                    "Cancellation of session {SessionId} on {Runner}: stopped={Stopped} {Explanation}",
                    sessionId,
                    kind,
                    stopped.Stopped,
                    stopped.Explanation);
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

    private async Task CancelPendingJobsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var pending = await _db.Jobs
            .Where(job => job.Type == JobType.Build && job.Status == JobStatus.Pending)
            .ToListAsync(cancellationToken);

        var affected = pending
            .Where(job => BuildJobPayload.TryParse(job.Payload)?.SessionId == sessionId)
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
