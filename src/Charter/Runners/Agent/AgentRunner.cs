using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Runners.Agent;

/// <summary>
/// The Charter Agent backend (sections 2.2, 33) — the primary runner, and the only one Phase 1 ships.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dispatch does not dispatch.</strong> Section 33.4 says the control plane never pushes, so
/// what <see cref="DispatchAsync"/> does is mark the session's work <em>claimable</em>: it writes one
/// row into the existing <c>jobs</c> queue, carrying the session's required capabilities plus the
/// marker that makes a row agent-claimable. An agent then asks for it over a socket it opened. No
/// second queue exists, and none should: the lease semantics, the <c>SKIP LOCKED</c> claim and the
/// expiry sweep that returns a crashed worker's jobs are already there and already tested.
/// </para>
/// <para>
/// The marker capability is what keeps the control plane's own <c>QueueDispatcher</c> from claiming
/// these rows straight back and dispatching them a second time. It is not a host capability and is
/// deliberately absent from <see cref="DescribeAsync"/>, so the union the dispatcher claims with
/// never contains it and the containment filter never matches an agent row.
/// </para>
/// </remarks>
public sealed class AgentRunner : IAgentRunner
{
    /// <summary>
    /// The synthetic capability every agent-claimable row requires.
    /// </summary>
    /// <remarks>
    /// Bare — no colon — so <c>RunnerCapability.Expand</c> yields it unchanged and the Postgres
    /// containment test needs no special case. Named so that a row in <c>psql</c> reads obviously as
    /// routing rather than as something an operator is expected to install.
    /// </remarks>
    public const string ClaimCapability = "charter_agent";

    private readonly IServiceScopeFactory _scopes;
    private readonly AgentConnectionRegistry _connections;
    private readonly AgentPlaneOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IServiceScopeFactory scopes,
        AgentConnectionRegistry connections,
        AgentPlaneOptions options,
        TimeProvider clock,
        ILogger<AgentRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _connections = connections;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public RunnerKind Kind => RunnerKind.Agent;

    /// <summary>The worker identity an agent claims under. Also what <c>jobs.claimed_by</c> holds.</summary>
    public static string WorkerIdFor(Guid agentId) => $"agent:{agentId:D}";

    /// <summary>The reverse: the agent behind a <c>claimed_by</c>, or null for another worker.</summary>
    public static Guid? AgentIdFrom(string? workerId) =>
        workerId is not null
        && workerId.StartsWith("agent:", StringComparison.Ordinal)
        && Guid.TryParse(workerId["agent:".Length..], out var agentId)
            ? agentId
            : null;

    /// <summary>
    /// What the registered agents advertise right now (sections 27.3, 32.2).
    /// </summary>
    /// <remarks>
    /// Re-read on every call rather than cached, because that is the point of section 32.2: a Mac
    /// mini that picked up an Xcode update must stop advertising the old version within one probe,
    /// not within one process lifetime. The union is taken over <em>online</em> agents only, while
    /// the descriptor still reports offline agents by name — because "your Mac mini is offline" is a
    /// far better message than "no runner has macOS".
    /// </remarks>
    public async ValueTask<RunnerDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var agents = await db.RunnerAgents
            .AsNoTracking()
            .Where(agent => agent.Status != RunnerAgentStatus.Revoked && agent.PairedAt != null)
            .Select(agent => new
            {
                agent.Id,
                agent.Name,
                agent.Status,
                agent.LastHeartbeatAt,
                agent.Capabilities,
            })
            .ToListAsync(cancellationToken);

        var now = _clock.GetUtcNow();
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        var onlineNames = new List<string>();

        foreach (var agent in agents)
        {
            var online = agent.Status == RunnerAgentStatus.Online
                && agent.LastHeartbeatAt is { } beat
                && beat + RunnerAgent.HeartbeatGrace >= now;

            if (!online)
            {
                continue;
            }

            onlineNames.Add(agent.Name);

            foreach (var capability in agent.Capabilities)
            {
                if (!string.Equals(capability, ClaimCapability, StringComparison.Ordinal))
                {
                    capabilities.Add(capability);
                }
            }
        }

        var name = agents.Count switch
        {
            0 => "Charter Agent (none registered)",
            _ when onlineNames.Count == 0 => $"Charter Agent ({agents.Count} registered, none online)",
            1 => $"Charter Agent ({onlineNames[0]})",
            _ => $"Charter Agent ({string.Join(", ", onlineNames)})",
        };

        return new RunnerDescriptor(RunnerKind.Agent, name, [.. capabilities], onlineNames.Count > 0);
    }

    /// <summary>
    /// Marks a session's work claimable. Safe to call twice with the same <c>DispatchKey</c>.
    /// </summary>
    /// <remarks>
    /// Idempotency is a primary key, not a check: the queue row's id is derived from the dispatch
    /// key, so a second call inserts a duplicate id and Postgres refuses it. That holds across a
    /// container restart, across two replicas, and across the window between the session journal's
    /// dispatch claim and this write (section 2.3).
    /// </remarks>
    public async Task<RunnerDispatchResult> DispatchAsync(
        RunnerDispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var jobId = SessionJournalId(dispatch.DispatchKey);

        if (await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == jobId, cancellationToken))
        {
            _logger.LogInformation(
                "Session {SessionId} is already claimable by an agent as job {JobId}",
                dispatch.SessionId,
                jobId);

            return RunnerDispatchResult.Ok(ExternalReferenceFor(jobId));
        }

        var payload = AgentJobPayload.From(dispatch, await CloneUrlAsync(db, dispatch.RepoFullName, cancellationToken));

        // The marker plus whatever the session declared. Expanding here keeps the Postgres containment
        // filter and the C# matcher from ever disagreeing about whether a job is claimable.
        var required = new List<string> { ClaimCapability };
        required.AddRange(RunnerCapability.ExpandAll(dispatch.RequiredCapabilities));

        var job = Job.Enqueue(
            JobType.Build,
            payload.ToJson(),
            requiredCapabilities: required,
            now: _clock.GetUtcNow(),
            id: jobId);

        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Another replica wrote the same deterministic id first. That is the guarantee working.
            _logger.LogInformation(
                exception,
                "Session {SessionId} was already made claimable by another dispatcher",
                dispatch.SessionId);

            return RunnerDispatchResult.Ok(ExternalReferenceFor(jobId));
        }

        _logger.LogInformation(
            "Session {SessionId} is claimable by a Charter Agent as job {JobId}, requiring {Capabilities}",
            dispatch.SessionId,
            jobId,
            string.Join(", ", required));

        return RunnerDispatchResult.Ok(ExternalReferenceFor(jobId));
    }

    /// <summary>
    /// Section 11: the cancel button must actually stop the runner.
    /// </summary>
    /// <remarks>
    /// Two cases, both handled. Work that is still queued is taken out of the queue, which is the
    /// common one — most cancels happen in the first minute. Work an agent already holds gets a
    /// <c>job.cancel</c> frame pushed down its own socket, and the claim is returned to the queue
    /// only after the agent has been told, so a slow acknowledgement cannot let a second runner start
    /// the same work while the first is still winding down.
    /// </remarks>
    public async Task<RunnerCancelResult> CancelAsync(
        RunnerCancellation cancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var jobId = TryParseReference(cancellation.ExternalReference);
        var job = jobId is { } id
            ? await db.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            : await FindBySessionAsync(db, cancellation.SessionId, cancellationToken);

        // The reference is folded from the session's events, and session_started arrives from the
        // execution plane, so a job id read out of it is a claim rather than a fact. Cancelling
        // somebody else's job and reporting confirmed would stop work nobody asked to stop and leave
        // this session running. Fall back to this session's own row instead.
        if (job is not null && AgentJobPayload.TryParse(job.Payload)?.SessionId != cancellation.SessionId)
        {
            job = await FindBySessionAsync(db, cancellation.SessionId, cancellationToken);
        }

        if (job is null)
        {
            return RunnerCancelResult.NothingToStop(
                "No agent job exists for this session, so there is nothing running to stop.");
        }

        switch (job.Status)
        {
            case JobStatus.Pending:
                job.Cancel(_clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                return RunnerCancelResult.Confirmed;

            case JobStatus.Claimed when AgentIdFrom(job.ClaimedBy) is { } agentId:
                {
                    var delivered = await _connections.CancelJobAsync(
                        agentId,
                        job.Id,
                        cancellation.Reason,
                        cancellationToken);

                    var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
                    await queue.FailAsync(
                        job.Id,
                        WorkerIdFor(agentId),
                        cancellation.Reason,
                        now: _clock.GetUtcNow(),
                        cancellationToken: cancellationToken);

                    return delivered
                        ? RunnerCancelResult.Confirmed
                        : RunnerCancelResult.NothingToStop(
                            "The agent holding this job is not connected right now. Its lease will lapse "
                            + "and the work will stop when it reconnects.");
                }

            default:
                return RunnerCancelResult.NothingToStop("The agent job had already finished.");
        }
    }

    /// <summary>The handle recorded on the dispatch event, so a restarted plane can still cancel.</summary>
    public static string ExternalReferenceFor(Guid jobId) => $"charter-agent:job:{jobId:D}";

    /// <summary>Reads a job id back out of <see cref="ExternalReferenceFor"/>.</summary>
    public static Guid? TryParseReference(string? reference)
    {
        const string prefix = "charter-agent:job:";

        return reference is not null
               && reference.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParse(reference[prefix.Length..], out var jobId)
            ? jobId
            : null;
    }

    /// <summary>
    /// Folds a dispatch key into the queue row's primary key.
    /// </summary>
    /// <remarks>
    /// A pure function of the key, which is what makes the second dispatch of a session a primary key
    /// violation rather than a second unit of work. The same trick <c>SessionJournal</c> uses for
    /// event ids, and for the same reason.
    /// </remarks>
    public static Guid SessionJournalId(string dispatchKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchKey);

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("charter-agent-job:" + dispatchKey));

        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The fallback for a cancel with no usable external reference: find this session's agent row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restricted to rows that require the routing marker, because the session id alone does not
    /// identify a plane. The control plane's own <c>BuildJobPayload</c> is <c>{"session_id": …}</c>
    /// and parses as an <see cref="AgentJobPayload"/> perfectly well, and a session can legitimately
    /// have one of each at the same instant — <c>QueueDispatcher</c> re-enqueues a pending control
    /// plane row every time a dispatch defers. Matching on the payload alone could therefore cancel
    /// the pending control plane row, report <em>confirmed</em>, and settle the session as cancelled
    /// while the agent holding the real claim was never told and went on to push a branch. Section 11
    /// says the cancel button must reach the runner; a cancel that reports success without reaching it
    /// is worse than one that fails.
    /// </para>
    /// <para>
    /// Ordered, so that two candidate rows resolve the same way on every replica and every retry.
    /// </para>
    /// </remarks>
    private static async Task<Job?> FindBySessionAsync(
        CharterDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs
            .Where(job => job.Type == JobType.Build
                          && (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed)
                          && job.RequiredCapabilities.Contains(ClaimCapability))
            .OrderByDescending(job => job.Status == JobStatus.Claimed)
            .ThenBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(job => AgentJobPayload.TryParse(job.Payload)?.SessionId == sessionId);
    }

    private static async Task<string?> CloneUrlAsync(
        CharterDbContext db,
        string repoFullName,
        CancellationToken cancellationToken)
    {
        var repo = await db.Repos
            .AsNoTracking()
            .Where(candidate => candidate.FullName == repoFullName)
            .Select(candidate => candidate.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        // Section A: the provider seam owns the real clone URL. Until it does, the GitHub form is the
        // one Phase 1 ships against, and the agent is handed a token scoped to exactly this repo.
        return repo is null ? null : $"https://github.com/{repo}.git";
    }
}
