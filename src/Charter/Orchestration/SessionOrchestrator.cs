using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>What one reconciliation pass decided about one session.</summary>
public sealed record SessionReconciliation(Guid SessionId, SessionRecoveryPlan Plan, bool Acted);

/// <summary>
/// The session orchestrator of section 2.1, and the answer to section 2.3's hardest constraint.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no in-memory orchestration state.</strong> Not "as little as possible" — none.
/// This service holds no dictionary of live sessions, no cancellation tokens keyed by session id, no
/// list of what it dispatched. Everything it knows it re-reads: the session row for status and the
/// cancel request, the event stream for what has been dispatched and how far the transcript got, the
/// job table for what is still queued.
/// </para>
/// <para>
/// The consequence is that a container that dies mid-session and one that has just booted are
/// indistinguishable from here, which is the property section 2.3 says forces a rewrite if it is
/// deferred. On startup it reclaims work whose lease expired, resumes each live session from its last
/// recorded <see cref="Event.Seq"/>, and — the part that matters — never dispatches a session a
/// backend already holds, because the dispatch is a row in Postgres rather than a fact this process
/// remembered.
/// </para>
/// <para>
/// It then keeps doing exactly the same thing on an interval. Recovery is not a special startup mode;
/// it is the steady state, which is why it is the code path that is always under test.
/// </para>
/// </remarks>
public sealed class SessionOrchestrator : BackgroundService
{
    /// <summary>Session states the orchestrator still has something to do about.</summary>
    private static readonly SessionStatus[] LiveStatuses =
    [
        SessionStatus.Queued,
        SessionStatus.Running,
        SessionStatus.NeedsInput,
    ];

    /// <summary>
    /// Session states whose run finished with something to review, and therefore want a recap.
    /// </summary>
    /// <remarks>
    /// Every state section 6 puts on or after <c>Running</c> that is not a failure. A session still
    /// <c>Queued</c> has not run; <c>Failed</c>, <c>Cancelled</c> and <c>Stale</c> produced nothing to
    /// orient anybody through; <c>NoChangesNeeded</c> is a success with, by definition, no change to
    /// read. <c>HandedOff</c> is included: an engineer who took the branch over is exactly the person
    /// who wants to know what the agent did to it before they arrived (section 7.5).
    /// </remarks>
    private static readonly SessionStatus[] RecappableStatuses =
    [
        SessionStatus.Running,
        SessionStatus.NeedsInput,
        SessionStatus.PrOpen,
        SessionStatus.PreviewReady,
        SessionStatus.InReview,
        SessionStatus.Merged,
        SessionStatus.HandedOff,
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(
        IServiceScopeFactory scopeFactory,
        OrchestrationOptions options,
        ILogger<SessionOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Reclaims lapsed leases and reconciles every live session. Called on startup and on an interval;
    /// public so a test can run one deterministic pass.
    /// </summary>
    /// <param name="startup">
    /// True for the first pass after boot. Only affects whether a <c>session_resumed</c> event is
    /// written — the decisions themselves do not depend on how long this process has been up, which is
    /// the point.
    /// </param>
    public async Task<IReadOnlyList<SessionReconciliation>> ReconcileAsync(
        bool startup = false,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<CharterDbContext>();
        var journal = provider.GetRequiredService<SessionJournal>();
        var coordinator = provider.GetRequiredService<SessionCoordinator>();
        var registry = provider.GetRequiredService<IRunnerRegistry>();
        var queue = provider.GetRequiredService<JobQueue>();

        // A worker that never came back still holds claims. Nothing else recovers that work.
        var reclaimed = await queue.ReleaseExpiredLeasesAsync(cancellationToken: cancellationToken);
        if (reclaimed > 0)
        {
            _logger.LogInformation("Reclaimed {Count} job(s) from expired leases", reclaimed);
        }

        var openJobs = await OpenBuildJobSessionsAsync(db, cancellationToken);

        // The gap either side of the spend gate. Approval writes the request's new status and the
        // build job in two transactions, so a container that dies between them leaves a request that
        // says "building this now" with nothing building it. Nothing else looks for that: the session
        // recovery below reconciles sessions, and this one does not have a session yet.
        await RecoverApprovedSpecsAsync(db, queue, openJobs, cancellationToken);

        // Section 14: a run that has finished and has no recap. Swept rather than triggered off the
        // callback that reported the completion, because the control plane can restart between them.
        await RequestRecapsAsync(db, queue, cancellationToken);

        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(session => LiveStatuses.Contains(session.Status))
            .OrderBy(session => session.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return [];
        }

        var reconciliations = new List<SessionReconciliation>(sessions.Count);

        // Change spec 001 part A: session → change request. Resolved optionally so a host that wires
        // no version control provider still reconciles; driven from here rather than from the runner's
        // result callback because the control plane can restart between the two and the change
        // request still has to be opened (section 2.3).
        var publisher = provider.GetService<ChangeRequestPublisher>();

        foreach (var session in sessions)
        {
            var summary = await journal.SummarizeAsync(session.Id, cancellationToken);

            var plan = SessionRecovery.Decide(new SessionRecoveryInput(
                session.Id,
                session.Status,
                session.CancelRequestedAt is not null,
                summary,
                openJobs.Contains(session.Id)));

            var acted = await ActAsync(session, plan, summary, coordinator, journal, registry, startup, cancellationToken);

            // The runner reported a clean completion and nothing has bound a change request to it.
            // Section 6 puts PROpen after Running, and this is the step that gets it there.
            if (publisher is not null
                && plan.Action == SessionRecoveryAction.Adopt
                && summary.TerminalReported
                && SessionRecovery.MapTerminal(summary.TerminalState!) is null)
            {
                try
                {
                    var publication = await publisher.PublishAsync(session.Id, cancellationToken);

                    acted |= publication.Outcome is ChangeRequestPublication.Opened
                        or ChangeRequestPublication.NoChanges;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One session's provider being unreachable must not stop the pass reconciling the
                    // other four hundred. The next pass tries again.
                    _logger.LogError(
                        exception,
                        "Could not open a change request for session {SessionId}",
                        session.Id);
                }
            }

            reconciliations.Add(new SessionReconciliation(session.Id, plan, acted));
        }

        return reconciliations;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reconciliations = await ReconcileAsync(startup, stoppingToken);

                if (startup)
                {
                    _logger.LogInformation(
                        "Session recovery examined {Count} live session(s): {Actions}",
                        reconciliations.Count,
                        string.Join(", ", reconciliations
                            .GroupBy(entry => entry.Plan.Action)
                            .Select(group => $"{group.Key}={group.Count()}")));

                    startup = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Session reconciliation failed; retrying");
            }

            try
            {
                await Task.Delay(_options.ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ActAsync(
        Session session,
        SessionRecoveryPlan plan,
        SessionJournalSummary summary,
        SessionCoordinator coordinator,
        SessionJournal journal,
        IRunnerRegistry registry,
        bool startup,
        CancellationToken cancellationToken)
    {
        switch (plan.Action)
        {
            case SessionRecoveryAction.Cancel:
                _logger.LogInformation("Recovering session {SessionId}: {Reason}", session.Id, plan.Reason);
                return await coordinator.CancelAsync(
                    session.Id,
                    "Cancellation was requested before the control plane restarted.",
                    cancellationToken);

            case SessionRecoveryAction.Settle:
                _logger.LogInformation("Recovering session {SessionId}: {Reason}", session.Id, plan.Reason);
                return await coordinator.SettleAsync(
                    session.Id,
                    plan.SettleAs!.Value,
                    plan.Reason,
                    cancellationToken);

            case SessionRecoveryAction.Dispatch:
                // Section 27.3 first: if nothing can run this, say so rather than queueing in silence.
                var routing = await registry.RouteAsync([], session.Runner, cancellationToken);
                if (!routing.IsRoutable)
                {
                    await coordinator.ExplainQueuedAsync(session.Id, [], routing, cancellationToken);
                    return false;
                }

                _logger.LogInformation("Re-queueing session {SessionId}: {Reason}", session.Id, plan.Reason);
                await coordinator.EnqueueAsync(
                    new BuildJobPayload { SessionId = session.Id },
                    cancellationToken: cancellationToken);
                return true;

            // Nothing has happened since the last restart said the same thing, so saying it again
            // would only be a record of the container's crash loop.
            case SessionRecoveryAction.Adopt when startup && summary.ResumedWithNoProgressSince:
                return false;

            case SessionRecoveryAction.Adopt when startup:
                // The resume marker is the visible half of section 2.3: an engineer reading the
                // transcript can see exactly where a restart happened and that nothing was replayed.
                var appended = await journal.AppendAsync(
                    session.Id,
                    OrchestrationEventTypes.SessionResumed,
                    new JsonObject
                    {
                        ["resumed_from_seq"] = plan.ResumeFromSeq,
                        ["runner"] = summary.Runner is { } kind ? SessionJournal.WireName(kind) : null,
                        ["reason"] = plan.Reason,
                    }.ToJsonString(),
                    $"resume:{session.Id:D}:{plan.ResumeFromSeq}",
                    cancellationToken: cancellationToken);

                if (appended.Appended)
                {
                    _logger.LogInformation(
                        "Adopted in-flight session {SessionId} from event {Seq} without re-dispatching",
                        session.Id,
                        plan.ResumeFromSeq);
                }

                return appended.Appended;

            default:
                return false;
        }
    }

    /// <summary>
    /// Re-queues specifications that were approved and never dispatched (section 2.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Approval and the job that acts on it are two writes, and the container can die between them.
    /// The recoverable half is the one that lands first — the approval, on the specification row —
    /// so this reads that and rebuilds the second. It is deliberately the <em>same</em> payload the
    /// API writes, going through the <em>same</em> handler, because a recovery path that takes a
    /// different route is a path nobody exercises until the day it matters.
    /// </para>
    /// <para>
    /// Nothing here is idempotent by luck. A specification that already has a session, or already has
    /// a build job naming it, is skipped; and if two replicas somehow both enqueued, the session id
    /// derived from the specification is the same for both and the second insert loses.
    /// </para>
    /// </remarks>
    public async Task<int> RecoverApprovedSpecsAsync(
        CharterDbContext db,
        JobQueue queue,
        IReadOnlySet<Guid> openJobs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(openJobs);

        var stranded = await (from request in db.Requests.AsNoTracking()
                              where request.Status == RequestStatus.Queued
                              join spec in db.Specs.AsNoTracking() on request.Id equals spec.RequestId
                              where spec.ApprovedAt != null
                                    && !db.Sessions.Any(session => session.SpecId == spec.Id)
                              orderby spec.ApprovedAt
                              select new { RequestId = request.Id, SpecId = spec.Id })
            .Take(100)
            .ToListAsync(cancellationToken);

        var recovered = 0;

        foreach (var entry in stranded)
        {
            if (openJobs.Contains(SpecBuildPayload.SessionIdFor(entry.SpecId)))
            {
                continue;
            }

            await queue.EnqueueAsync(
                JobType.Build,
                new SpecBuildPayload { RequestId = entry.RequestId, SpecId = entry.SpecId }.ToJson(),
                cancellationToken: cancellationToken);

            recovered++;

            _logger.LogInformation(
                "Re-queued approved specification {SpecId} on request {RequestId}: it was approved and "
                + "never dispatched",
                entry.SpecId,
                entry.RequestId);
        }

        return recovered;
    }

    /// <summary>
    /// Queues the engineer recap for every session whose run has finished and has none (section 14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sweep rather than a continuation off the completion callback, for section 2.3's reason: the
    /// control plane can restart between a run ending and anybody noticing, and a callback that fired
    /// into a process that is no longer there leaves the engineer with no recap and nothing to say so.
    /// The trigger is therefore a fact in Postgres — a <c>session_ended</c> event, a session that did
    /// not fail, and no recap row — which is true whether or not this process was running at the time.
    /// </para>
    /// <para>
    /// Failures are excluded deliberately. Section 14's recap orients an engineer through a change
    /// they are about to review, and a session that failed produced nothing to review; section 11
    /// already routes that to an engineer by a different path.
    /// </para>
    /// </remarks>
    public async Task<int> RequestRecapsAsync(
        CharterDbContext db,
        JobQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(queue);

        var finished = await db.Sessions
            .AsNoTracking()
            .Where(session => RecappableStatuses.Contains(session.Status))
            .Where(session => db.Events.Any(@event =>
                @event.SessionId == session.Id && @event.Type == EventTypes.SessionEnded))
            .Where(session => !db.Recaps.Any(recap => recap.SessionId == session.Id))
            .OrderBy(session => session.CreatedAt)
            .Take(50)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

        if (finished.Count == 0)
        {
            return 0;
        }

        // The queue itself is the other half of the guard. The sweep runs every fifteen seconds, and
        // without this it would queue one recap per pass for as long as the row took to be claimed.
        var queued = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Recap
                          && (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed))
            .Select(job => job.Payload)
            .ToListAsync(cancellationToken);

        var alreadyQueued = queued
            .Select(payload => RecapJobPayload.TryParse(payload)?.SessionId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        var requested = 0;

        foreach (var sessionId in finished.Where(id => !alreadyQueued.Contains(id)))
        {
            await queue.EnqueueAsync(
                JobType.Recap,
                new RecapJobPayload { SessionId = sessionId }.ToJson(),
                cancellationToken: cancellationToken);

            requested++;

            _logger.LogInformation("Queued the engineer recap for session {SessionId}", sessionId);
        }

        return requested;
    }

    /// <summary>Sessions that already have a build job waiting or in flight.</summary>
    /// <remarks>
    /// Both payload shapes count. An approval-shaped job names a specification rather than a session,
    /// and the session id is a pure function of it — so a job that has not been claimed yet still
    /// answers "somebody is going to build this", and the sweep does not enqueue a second one.
    /// </remarks>
    private static async Task<HashSet<Guid>> OpenBuildJobSessionsAsync(
        CharterDbContext db,
        CancellationToken cancellationToken)
    {
        var payloads = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Build
                          && (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed))
            .Select(job => job.Payload)
            .ToListAsync(cancellationToken);

        var sessions = new HashSet<Guid>();
        var specs = new HashSet<Guid>();

        foreach (var payload in payloads)
        {
            if (BuildJobPayload.TryParse(payload) is { } parsed)
            {
                sessions.Add(parsed.SessionId);
                continue;
            }

            if (SpecBuildPayload.TryParse(payload) is { } approved)
            {
                specs.Add(approved.SpecId);

                if (!approved.IsRebuild)
                {
                    sessions.Add(SpecBuildPayload.SessionIdFor(approved.SpecId));
                }
            }
        }

        if (specs.Count > 0)
        {
            // A rebuild's session id is derived from a feedback row, so it cannot be computed here.
            // Every live session on a specification a queued job names is covered by that job.
            var onSpec = await db.Sessions
                .AsNoTracking()
                .Where(session => specs.Contains(session.SpecId))
                .Select(session => session.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in onSpec)
            {
                sessions.Add(id);
            }
        }

        return sessions;
    }
}
