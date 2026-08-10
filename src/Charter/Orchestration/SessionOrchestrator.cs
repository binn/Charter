using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
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

        var openJobs = await OpenBuildJobSessionsAsync(db, cancellationToken);
        var reconciliations = new List<SessionReconciliation>(sessions.Count);

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

    /// <summary>Sessions that already have a build job waiting or in flight.</summary>
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

        foreach (var payload in payloads)
        {
            if (BuildJobPayload.TryParse(payload) is { } parsed)
            {
                sessions.Add(parsed.SessionId);
            }
        }

        return sessions;
    }
}
