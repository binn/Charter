using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Runners;

/// <summary>
/// <c>GET /api/runners</c> — the registered Charter Agents and what is waiting on one.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.2a: an instance serves exactly one organisation, so every agent registered on it belongs
/// to that organisation. The <c>org_id</c> filter below is load-bearing for audit and for queries, not
/// a tenancy boundary — and an earlier review's worry that the runner registry advertises every
/// online agent is, under that rule, correct behaviour.
/// </para>
/// <para>
/// <see cref="QueuedSessionDemandResponse.EligibleAgentIds"/> is computed here rather than in the
/// client. The UI renders the reasoning; the server owns the verdict — and the verdict is the same
/// set-containment test the job queue applies when an agent asks for work, so an agent named here is
/// one that could actually claim the job.
/// </para>
/// </remarks>
public sealed class RunnersQueryService
{
    private readonly CharterDbContext database;
    private readonly TimeProvider clock;

    public RunnersQueryService(CharterDbContext database, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.clock = clock;
    }

    /// <summary>Everything Settings → Runners shows.</summary>
    public async Task<RunnersViewResponse> DescribeAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var now = clock.GetUtcNow();

        var agents = await database.RunnerAgents
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId)
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);

        var inFlight = await InFlightAsync(agents.Select(agent => agent.Id), now, cancellationToken);

        return new RunnersViewResponse
        {
            Agents = [.. agents.Select(agent => Describe(agent, inFlight.GetValueOrDefault(agent.Id), now))],
            Waiting = await WaitingAsync(member.OrgId, agents, cancellationToken),
        };
    }

    /// <summary>One agent row.</summary>
    public static RunnerAgentResponse Describe(RunnerAgent agent, int inFlight, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var compatible = AgentProtocol.Supports(agent.ProtocolVersion);

        return new RunnerAgentResponse
        {
            Id = agent.Id.ToString(),
            Name = agent.Name,
            Mode = agent.Mode.ToApi(),
            Version = agent.AgentVersion,
            ProtocolCompatible = compatible,

            // Section 33.6: a mismatch produces a clear message and a refusal to claim work, rather
            // than subtle failures three sessions later. This is that message.
            ProtocolNote = compatible ? null : AgentProtocol.DescribeMismatch(agent.ProtocolVersion),
            Status = StatusOf(agent, inFlight, now),
            LastHeartbeatAt = agent.LastHeartbeatAt,

            // When it became this instance's problem: the pairing, or the invitation for an agent
            // that never spent its token.
            RegisteredAt = agent.PairedAt ?? agent.CreatedAt,
            Capabilities = AgentCapabilityProbes.Describe(
                agent.Capabilities,
                agent.CapabilitiesProbedAt ?? agent.PairedAt ?? agent.CreatedAt),
            Concurrency = new AgentConcurrencyResponse { Limit = agent.Concurrency, InFlight = inFlight },

            // Empty rather than invented. An agent that has never connected has reported no platform,
            // and "unknown" is a fact the list can render.
            Os = agent.Os ?? string.Empty,
            Arch = agent.Arch ?? string.Empty,
        };
    }

    /// <summary>
    /// Where an agent stands, reading both halves of section 33.3's liveness.
    /// </summary>
    /// <remarks>
    /// The status column is what a connect and a disconnect write; the heartbeat window covers the
    /// case the column cannot, which is a control plane killed mid-connection that never wrote the
    /// disconnect. An agent that has stopped heartbeating while still holding leases is
    /// <c>draining</c>: it will claim nothing new, and its jobs return to the queue when the leases
    /// lapse. Saying <c>offline</c> there would be a smaller truth than the operator needs.
    /// </remarks>
    public static ApiAgentStatus StatusOf(RunnerAgent agent, int inFlight, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.IsRevoked)
        {
            return ApiAgentStatus.Revoked;
        }

        if (agent.IsOnlineAt(now))
        {
            return ApiAgentStatus.Online;
        }

        return inFlight > 0 ? ApiAgentStatus.Draining : ApiAgentStatus.Offline;
    }

    /// <summary>
    /// Whether an agent could claim a job needing these capabilities.
    /// </summary>
    /// <remarks>
    /// Offline is not disqualifying: a Mac mini that is switched off is the reason a session queues
    /// rather than fails (section 27.3), and telling the operator that nothing on the instance can
    /// ever run it would be wrong. A revoked credential and an incompatible protocol are
    /// disqualifying, because neither will claim work again without somebody doing something.
    /// </remarks>
    public static bool IsEligible(RunnerAgent agent, IReadOnlyList<string> required)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(required);

        return !agent.IsRevoked
               && AgentProtocol.Supports(agent.ProtocolVersion)
               && RunnerCapability.Missing(agent.Capabilities.ToHashSet(StringComparer.Ordinal), required).Count == 0;
    }

    /// <summary>Jobs each agent holds a live lease on right now (section 33.4).</summary>
    private async Task<IReadOnlyDictionary<Guid, int>> InFlightAsync(
        IEnumerable<Guid> agentIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var workers = agentIds.ToDictionary(AgentRunner.WorkerIdFor, id => id, StringComparer.Ordinal);

        if (workers.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = workers.Keys.ToList();

        var claimed = await database.Jobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Claimed
                          && job.ClaimedBy != null
                          && ids.Contains(job.ClaimedBy)
                          && job.LeaseExpiresAt > now)
            .Select(job => job.ClaimedBy!)
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<Guid, int>();

        foreach (var worker in claimed)
        {
            if (workers.TryGetValue(worker, out var agentId))
            {
                counts[agentId] = counts.GetValueOrDefault(agentId) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// The sessions waiting on a runner, with what each needs (section 27.3).
    /// </summary>
    /// <remarks>
    /// A pending build job with required capabilities <em>is</em> a waiting session: the queue's
    /// capability filter is what keeps an unclaimable job pending rather than dispatching it into a
    /// failure. So this reads the queue rather than a second list that could disagree with it.
    /// </remarks>
    private async Task<IReadOnlyList<QueuedSessionDemandResponse>> WaitingAsync(
        Guid orgId,
        IReadOnlyList<RunnerAgent> agents,
        CancellationToken cancellationToken)
    {
        var pending = await database.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Build && job.Status == JobStatus.Pending)
            .OrderBy(job => job.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return [];
        }

        var requests = await database.Requests
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .ToListAsync(cancellationToken);

        var specs = await database.Specs
            .AsNoTracking()
            .Where(row => requests.Select(request => request.Id).Contains(row.RequestId))
            .ToListAsync(cancellationToken);

        var sessions = await database.Sessions
            .AsNoTracking()
            .Where(row => specs.Select(spec => spec.Id).Contains(row.SpecId))
            .ToListAsync(cancellationToken);

        var byRequest = requests.ToDictionary(row => row.Id);
        var specToRequest = specs.ToDictionary(row => row.Id, row => row.RequestId);
        var sessionToRequest = sessions
            .Where(row => specToRequest.ContainsKey(row.SpecId))
            .ToDictionary(row => row.Id, row => specToRequest[row.SpecId]);

        var waiting = new List<QueuedSessionDemandResponse>();

        foreach (var job in pending)
        {
            var requestId = ResolveRequest(job.Payload, byRequest.Keys, specToRequest, sessionToRequest);
            if (requestId is not { } id || !byRequest.TryGetValue(id, out var request))
            {
                continue;
            }

            var required = job.RequiredCapabilities;
            var eligible = agents.Where(agent => IsEligible(agent, required)).ToList();

            waiting.Add(new QueuedSessionDemandResponse
            {
                RequestId = id.ToString(),
                Title = specs
                    .Where(spec => spec.RequestId == id)
                    .OrderByDescending(spec => spec.Version)
                    .Select(spec => spec.Title)
                    .FirstOrDefault() ?? RequestPresentation.TitleFrom(request.RawText),
                Requires = required,
                EligibleAgentIds = [.. eligible.Select(agent => agent.Id.ToString())],
                QueuedReason = Explain(required, eligible, agents, clock.GetUtcNow()),
            });
        }

        return waiting;
    }

    /// <summary>
    /// Why a session is still waiting, in the shape section 27.3 gives.
    /// </summary>
    /// <remarks>
    /// Three cases, and they need different sentences. Nothing matches at all: name the capabilities
    /// and where to register one. Something would match but is on the wrong protocol: say so, because
    /// section 33.6 promises a clear message rather than a session that mysteriously never starts.
    /// Something matches and is switched off: say which, because the fix is to switch it on.
    /// </remarks>
    private static string? Explain(
        IReadOnlyList<string> required,
        IReadOnlyList<RunnerAgent> eligible,
        IReadOnlyList<RunnerAgent> agents,
        DateTimeOffset now)
    {
        if (eligible.Any(agent => agent.IsOnlineAt(now)))
        {
            // Something can take it. It is queued because the queue has not got to it yet, which
            // needs no explanation beyond the queue itself.
            return null;
        }

        if (eligible.Count > 0)
        {
            var names = string.Join(", ", eligible.Select(agent => agent.Name));
            return $"{names} can run this and is not connected right now. It will claim the job when "
                   + "it comes back online.";
        }

        var incompatible = agents
            .Where(agent => !agent.IsRevoked
                            && !AgentProtocol.Supports(agent.ProtocolVersion)
                            && RunnerCapability.Missing(
                                agent.Capabilities.ToHashSet(StringComparer.Ordinal),
                                required).Count == 0)
            .ToList();

        if (incompatible.Count > 0)
        {
            var names = string.Join(", ", incompatible.Select(agent => $"{agent.Name} ({agent.AgentVersion})"));
            return $"{names} has everything this needs but speaks an agent protocol this Charter does "
                   + "not, and will not claim work until it is upgraded.";
        }

        var missing = RunnerCapability.Missing(
            agents.SelectMany(agent => agent.Capabilities).ToHashSet(StringComparer.Ordinal),
            required);

        return missing.Count == 0
            ? $"No runner is available for this session yet. {RunnerRegistry.RegisterHint}"
            : $"No runner available with {RunnerCapability.DescribeAll(missing)}. {RunnerRegistry.RegisterHint}";
    }

    /// <summary>
    /// The request a queued build job belongs to.
    /// </summary>
    /// <remarks>
    /// Section 5 gives a job no foreign keys on purpose, and the payload's spelling depends on who
    /// wrote it — intake writes a spec id, the orchestrator writes a session id. So this matches on
    /// any identifier in the payload that names work in this organisation, which cannot go stale the
    /// way a hard-coded property name would.
    /// </remarks>
    private static Guid? ResolveRequest(
        string payload,
        IEnumerable<Guid> requestIds,
        IReadOnlyDictionary<Guid, Guid> specToRequest,
        IReadOnlyDictionary<Guid, Guid> sessionToRequest)
    {
        var identifiers = Identifiers(payload);

        foreach (var id in identifiers)
        {
            if (specToRequest.TryGetValue(id, out var fromSpec))
            {
                return fromSpec;
            }

            if (sessionToRequest.TryGetValue(id, out var fromSession))
            {
                return fromSession;
            }
        }

        var requests = requestIds.ToHashSet();
        return identifiers.FirstOrDefault(requests.Contains) is { } match && match != Guid.Empty ? match : null;
    }

    private static IReadOnlyList<Guid> Identifiers(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var found = new List<Guid>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var id))
                {
                    found.Add(id);
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
