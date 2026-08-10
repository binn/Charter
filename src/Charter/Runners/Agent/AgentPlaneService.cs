using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Runners.Agent;

/// <summary>What generating a pairing token produced. The token is shown once and never again.</summary>
/// <param name="AgentId">The row the token will pair.</param>
/// <param name="PairingToken">Hand this to <c>charter-agent --token</c>.</param>
/// <param name="ExpiresAt">Section 33.3: short-TTL.</param>
public sealed record RunnerAgentInvitation(Guid AgentId, string PairingToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RunnerAgentInvitation {{ AgentId = {AgentId}, PairingToken = redacted }}";
}

/// <summary>The five ways a pairing attempt ends, mapped to the status codes the daemon acts on.</summary>
public enum AgentPairingOutcome
{
    /// <summary>200. The credential is in the response and nowhere else.</summary>
    Paired,

    /// <summary>401. No such token. The daemon does not retry.</summary>
    TokenRejected,

    /// <summary>403. The agent this token names has been revoked. The daemon does not retry.</summary>
    Revoked,

    /// <summary>410. Spent or expired. The daemon says "generate a fresh one" and stops.</summary>
    TokenExpired,

    /// <summary>426. No protocol version in common (section 33.6).</summary>
    ProtocolRefused,
}

/// <summary>The outcome of trading a pairing token for a credential.</summary>
public sealed record AgentPairingResult(AgentPairingOutcome Outcome, PairResponse? Response, string Message)
{
    public bool Ok => Outcome == AgentPairingOutcome.Paired;

    /// <summary>The stable machine code the <c>error</c> field of the failure body carries.</summary>
    public string ErrorCode => Outcome switch
    {
        AgentPairingOutcome.TokenRejected => AgentErrorCodes.PairingTokenRejected,
        AgentPairingOutcome.TokenExpired => AgentErrorCodes.PairingTokenExpired,
        AgentPairingOutcome.Revoked => AgentErrorCodes.CredentialRevoked,
        AgentPairingOutcome.ProtocolRefused => AgentErrorCodes.ProtocolUnsupported,
        _ => string.Empty,
    };

    public override string ToString() => $"AgentPairingResult {{ Outcome = {Outcome} }}";
}

/// <summary>How authenticating a presented agent credential ended.</summary>
public enum AgentAuthOutcome
{
    Authenticated,

    /// <summary>No such agent, a malformed token, or the wrong secret. Answered as 401.</summary>
    Unknown,

    /// <summary>The row exists and has been revoked. Answered as 403 (section 33.3).</summary>
    Revoked,
}

/// <summary>The result of authenticating a connect request.</summary>
public sealed record AgentAuthentication(AgentAuthOutcome Outcome, Guid AgentId, string Name)
{
    public static AgentAuthentication Unknown { get; } = new(AgentAuthOutcome.Unknown, Guid.Empty, string.Empty);

    public bool Ok => Outcome == AgentAuthOutcome.Authenticated;
}

/// <summary>One row of the runners list (sections 27.3, 33.3).</summary>
public sealed record RunnerAgentView(
    Guid Id,
    string Name,
    string Mode,
    string AgentVersion,
    int ProtocolVersion,
    string Status,
    bool Online,
    int Concurrency,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? CapabilitiesProbedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PairedAt,
    string? Hostname,
    string? Os,
    string? Rid);

/// <summary>
/// Registration, authentication and revocation for the Charter Agent (section 33.3).
/// </summary>
/// <remarks>
/// Scoped, because everything it does is one short database transaction. The long-lived half of an
/// agent's life is <see cref="AgentConnection"/>, which opens its own scope per frame for exactly the
/// reason a request-scoped context must not be held open for hours.
/// </remarks>
public sealed class AgentPlaneService
{
    private readonly CharterDbContext _db;
    private readonly JobQueue _queue;
    private readonly AgentCredentialMint _mint;
    private readonly AgentConnectionRegistry _connections;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentPlaneService> _logger;

    public AgentPlaneService(
        CharterDbContext db,
        JobQueue queue,
        AgentCredentialMint mint,
        AgentConnectionRegistry connections,
        TimeProvider clock,
        ILogger<AgentPlaneService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(mint);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _queue = queue;
        _mint = mint;
        _connections = connections;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Step 1 of section 33.3: an admin generates a single-use, short-TTL pairing token.</summary>
    public async Task<RunnerAgentInvitation> InviteAsync(
        Guid orgId,
        string name,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7();
        var minted = _mint.IssuePairingToken(id);
        var now = _clock.GetUtcNow();

        var agent = RunnerAgent.Invite(orgId, name, minted.Hash, lifetime, now, id);

        _db.RunnerAgents.Add(agent);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Generated a pairing token for agent {AgentId} ({Name}) in organisation {OrgId}, valid until {ExpiresAt:O}",
            agent.Id,
            agent.Name,
            orgId,
            agent.PairingTokenExpiresAt);

        return new RunnerAgentInvitation(agent.Id, minted.Token, agent.PairingTokenExpiresAt!.Value);
    }

    /// <summary>
    /// Step 2: trades the pairing token for the long-lived credential.
    /// </summary>
    /// <remarks>
    /// Single use is enforced twice over. <see cref="RunnerAgent.CompletePairing"/> clears the
    /// verifier, so a replayed token finds nothing to check; and the row's concurrency token makes two
    /// simultaneous attempts a race exactly one of them wins, so "single-use" survives two agents
    /// started from the same copied command line in the same second.
    /// </remarks>
    public async Task<AgentPairingResult> PairAsync(
        PairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgentProtocol.Supports(request.ProtocolVersion))
        {
            return new AgentPairingResult(
                AgentPairingOutcome.ProtocolRefused,
                null,
                AgentProtocol.DescribeMismatch(request.ProtocolVersion));
        }

        if (!AgentCredentialMint.TryParse(
                request.PairingToken,
                AgentCredentialMint.PairingTokenPrefix,
                out var agentId,
                out var secret))
        {
            _mint.SpendEquivalentWork();
            return Rejected();
        }

        var agent = await _db.RunnerAgents.FirstOrDefaultAsync(
            candidate => candidate.Id == agentId,
            cancellationToken);

        if (agent is null)
        {
            _mint.SpendEquivalentWork();
            return Rejected();
        }

        if (agent.IsRevoked)
        {
            _mint.SpendEquivalentWork();
            return new AgentPairingResult(
                AgentPairingOutcome.Revoked,
                null,
                "This agent has been revoked. Register a new one in Settings → Runners.");
        }

        var now = _clock.GetUtcNow();

        // Spend the verification work before deciding, so a spent token and a wrong secret take the
        // same time and neither is an oracle for the other.
        var verified = _mint.Verify(agent.PairingTokenHash, secret);

        if (!agent.PairingTokenIsLiveAt(now))
        {
            return new AgentPairingResult(
                AgentPairingOutcome.TokenExpired,
                null,
                "This pairing token has expired or was already used. Generate a fresh one in "
                + "Settings → Runners.");
        }

        if (!verified)
        {
            return Rejected();
        }

        var credential = _mint.IssueAgentToken(agent.Id);

        agent.CompletePairing(
            credential.Hash,
            request.Name,
            AgentConnection.ParseMode(request.Mode),
            request.AgentVersion,
            AgentProtocol.Version,
            request.Concurrency,
            new RunnerAgentPlatform(
                request.Platform.Os,
                request.Platform.Arch,
                request.Platform.Rid,
                request.Platform.Hostname,
                request.Platform.CpuCount,
                request.Platform.TotalMemoryMb),
            RunnerCapability.ExpandAll(request.Capabilities),
            now);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two agents raced for one token. The loser is told the truth: it has been used.
            return new AgentPairingResult(
                AgentPairingOutcome.TokenExpired,
                null,
                "This pairing token was used by another agent. Generate a fresh one in "
                + "Settings → Runners.");
        }

        _logger.LogInformation(
            "Agent {AgentId} ({Name}) paired: {Mode} mode, charter-agent {AgentVersion}, {Count} capabilities",
            agent.Id,
            agent.Name,
            agent.Mode,
            agent.AgentVersion,
            agent.Capabilities.Count);

        return new AgentPairingResult(
            AgentPairingOutcome.Paired,
            new PairResponse
            {
                AgentId = agent.Id.ToString("D"),
                AgentToken = credential.Token,
                ProtocolVersion = AgentProtocol.Version,
            },
            "Paired.");
    }

    /// <summary>
    /// Authenticates a presented agent credential on the connect request.
    /// </summary>
    /// <remarks>
    /// A revoked agent has no verifier at all — <see cref="RunnerAgent.Revoke"/> nulls it — so this
    /// would refuse it even if the status check below were deleted. The status is still read, because
    /// 403 and 401 mean different things to an operator reading the daemon's log.
    /// </remarks>
    public async Task<AgentAuthentication> AuthenticateAsync(
        string? presentedToken,
        CancellationToken cancellationToken = default)
    {
        if (!AgentCredentialMint.TryParse(
                presentedToken,
                AgentCredentialMint.AgentTokenPrefix,
                out var agentId,
                out var secret))
        {
            _mint.SpendEquivalentWork();
            return AgentAuthentication.Unknown;
        }

        var agent = await _db.RunnerAgents
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == agentId, cancellationToken);

        if (agent is null)
        {
            _mint.SpendEquivalentWork();
            return AgentAuthentication.Unknown;
        }

        if (agent.IsRevoked)
        {
            _mint.SpendEquivalentWork();
            return new AgentAuthentication(AgentAuthOutcome.Revoked, agent.Id, agent.Name);
        }

        return _mint.Verify(agent.CredentialHash, secret)
            ? new AgentAuthentication(AgentAuthOutcome.Authenticated, agent.Id, agent.Name)
            : AgentAuthentication.Unknown;
    }

    /// <summary>
    /// Section 33.3: revoke instantly — kill in-flight jobs, push <c>revoked</c>, invalidate the
    /// credential.
    /// </summary>
    /// <remarks>
    /// The order matters. The credential dies in Postgres first, so an agent that reconnects during
    /// the next two milliseconds is refused rather than resuming. Its claims go back to the queue
    /// next, so the work is runnable by somebody else whether or not the agent ever hears about it.
    /// The frame goes last, and is best-effort: a revoked agent that is switched off is still revoked.
    /// </remarks>
    public async Task<bool> RevokeAsync(
        Guid agentId,
        string reason = "Revoked by an administrator.",
        CancellationToken cancellationToken = default)
    {
        var agent = await _db.RunnerAgents.FirstOrDefaultAsync(
            candidate => candidate.Id == agentId,
            cancellationToken);

        if (agent is null)
        {
            return false;
        }

        var now = _clock.GetUtcNow();

        if (!agent.IsRevoked)
        {
            agent.Revoke(reason, now);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var released = await _queue.ReleaseWorkerClaimsAsync(
            AgentRunner.WorkerIdFor(agentId),
            now,
            cancellationToken);

        var pushed = await _connections.RevokeAsync(agentId, reason, cancellationToken);

        _logger.LogWarning(
            "Agent {AgentId} ({Name}) revoked: {Reason}. {Released} job(s) returned to the queue; frame delivered: {Pushed}",
            agentId,
            agent.Name,
            reason,
            released,
            pushed);

        return true;
    }

    /// <summary>Everything an admin sees in Settings → Runners for one organisation.</summary>
    public async Task<IReadOnlyList<RunnerAgentView>> ListAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var agents = await _db.RunnerAgents
            .AsNoTracking()
            .Where(agent => agent.OrgId == orgId)
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

        return [.. agents.Select(agent => new RunnerAgentView(
            agent.Id,
            agent.Name,
            EnumDbNames<RunnerAgentMode>.ToDb(agent.Mode),
            agent.AgentVersion,
            agent.ProtocolVersion,
            EnumDbNames<RunnerAgentStatus>.ToDb(agent.Status),
            agent.IsOnlineAt(now),
            agent.Concurrency,
            agent.Capabilities,
            agent.CapabilitiesProbedAt,
            agent.LastHeartbeatAt,
            agent.CreatedAt,
            agent.PairedAt,
            agent.Hostname,
            agent.Os,
            agent.Rid))];
    }

    private static AgentPairingResult Rejected() =>
        new(
            AgentPairingOutcome.TokenRejected,
            null,
            "The pairing token was rejected. Pairing tokens are single-use and short-lived - "
            + "generate a fresh one in Settings → Runners.");
}
