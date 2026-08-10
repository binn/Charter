namespace Charter.Domain;

/// <summary>How a Charter Agent runs the work it claims (section 33.2).</summary>
public enum RunnerAgentMode
{
    /// <summary>Ephemeral containers over the host's own Docker socket. The supported default.</summary>
    Docker,

    /// <summary>
    /// Directly on the host, under a dedicated unprivileged account. Exists because macOS with Xcode
    /// cannot be containerised and USB-attached targets are awkward to pass through.
    /// </summary>
    Native,
}

/// <summary>Where a registered agent stands right now (section 33.3).</summary>
public enum RunnerAgentStatus
{
    /// <summary>Registered but not connected. Never routed to; still explains itself in the UI.</summary>
    Offline,

    /// <summary>Connected and heartbeating. The only status that claims work.</summary>
    Online,

    /// <summary>
    /// Revoked by an admin. The credential is gone, in-flight work was killed, and a reconnect with
    /// the old credential is refused rather than merely ignored.
    /// </summary>
    Revoked,
}

/// <summary>
/// Facts an agent establishes about its own host and reports on pairing and on every connect.
/// </summary>
/// <remarks>
/// Probed, never declared (section 32.2). Held so the runners list can say <em>your Mac mini</em>
/// rather than <em>agent 0192…</em>, and so section 33.6's update offer can name the right asset.
/// </remarks>
public sealed record RunnerAgentPlatform(
    string Os,
    string Arch,
    string Rid,
    string Hostname,
    int CpuCount,
    long? TotalMemoryMb = null);

/// <summary>
/// A Charter Agent this instance knows about (section 33).
/// </summary>
/// <remarks>
/// <para>
/// The row exists from the moment an admin generates a pairing token, which is what makes the token
/// single-use without a second table: the token's hash lives on the row it will pair, and pairing
/// clears it in the same write that stores the long-lived credential. A token that has been spent is
/// therefore indistinguishable from one that never existed, and both answer the same way.
/// </para>
/// <para>
/// <strong>Neither credential is ever stored.</strong> The pairing token and the long-lived agent
/// credential are both bearer secrets, so what this row holds is a verifier — a salted PBKDF2 hash
/// produced by <c>ICharterPasswordHasher</c> — and never the value itself. A database dump, a backup,
/// or a support query therefore cannot be replayed against the connect endpoint.
/// </para>
/// </remarks>
public sealed class RunnerAgent : IVersionedEntity
{
    /// <summary>Section 33.3: pairing tokens are short-TTL. Long enough to paste, short enough to lose.</summary>
    public static readonly TimeSpan DefaultPairingTokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after its last heartbeat an agent is still treated as online.
    /// </summary>
    /// <remarks>
    /// Deliberately several heartbeat intervals: a single missed beat over a domestic connection is
    /// noise, and marking a Mac mini offline for it would bounce every queued session through the
    /// no-eligible-runner explanation of section 27.3.
    /// </remarks>
    public static readonly TimeSpan HeartbeatGrace = TimeSpan.FromMinutes(2);

    /// <summary>Section 33.4: conservative by default.</summary>
    public const int DefaultConcurrency = 2;

    private RunnerAgent()
    {
    }

    private RunnerAgent(
        Guid id,
        Guid orgId,
        string name,
        string pairingTokenHash,
        DateTimeOffset pairingTokenExpiresAt,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        Name = name;
        PairingTokenHash = pairingTokenHash;
        PairingTokenExpiresAt = pairingTokenExpiresAt;
        Status = RunnerAgentStatus.Offline;
        Concurrency = DefaultConcurrency;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    /// <summary>What the operator called it, replaced by the agent's own <c>--name</c> on pairing.</summary>
    public string Name { get; private set; } = string.Empty;

    public RunnerAgentMode Mode { get; private set; }

    /// <summary>The <c>charter-agent</c> build that last connected.</summary>
    public string AgentVersion { get; private set; } = string.Empty;

    /// <summary>The version agreed on the last successful handshake (section 33.6).</summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>
    /// Already expanded by <c>RunnerCapability.ExpandAll</c>, so matching is set containment and the
    /// Postgres <c>&lt;@</c> filter in the job queue can never disagree with the C# matcher.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; private set; } = [];

    /// <summary>The agent's own hash of what it advertised, so drift is a cheap comparison.</summary>
    public string? CapabilitiesHash { get; private set; }

    /// <summary>Section 32.2: re-probed on restart and daily, never declared.</summary>
    public DateTimeOffset? CapabilitiesProbedAt { get; private set; }

    /// <summary>Most jobs this agent will hold at once (section 33.4).</summary>
    public int Concurrency { get; private set; }

    public RunnerAgentStatus Status { get; private set; }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    /// <summary>PBKDF2 verifier for the long-lived credential. Null before pairing and after revocation.</summary>
    public string? CredentialHash { get; private set; }

    /// <summary>PBKDF2 verifier for the single-use pairing token. Null once it has been spent.</summary>
    public string? PairingTokenHash { get; private set; }

    public DateTimeOffset? PairingTokenExpiresAt { get; private set; }

    public string? Os { get; private set; }

    public string? Arch { get; private set; }

    public string? Rid { get; private set; }

    public string? Hostname { get; private set; }

    public int CpuCount { get; private set; }

    /// <summary>When the admin generated the pairing token.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the agent spent it. Null while the invitation is outstanding.</summary>
    public DateTimeOffset? PairedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public int Version { get; private set; }

    /// <summary>True once a credential exists to authenticate a connection with.</summary>
    public bool IsPaired => PairedAt is not null && CredentialHash is not null;

    public bool IsRevoked => Status == RunnerAgentStatus.Revoked;

    /// <summary>True while the pairing token can still be spent.</summary>
    public bool PairingTokenIsLiveAt(DateTimeOffset now)
        => PairingTokenHash is not null
           && PairingTokenExpiresAt is { } expires
           && expires > now.ToUniversalTime();

    /// <summary>
    /// True when this agent may be routed to.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. The status column is what a connect and a disconnect write, and the
    /// heartbeat window is what covers the case the status column cannot: a control plane that was
    /// killed mid-connection never wrote the disconnect, so a stale <c>online</c> row would otherwise
    /// keep advertising a Mac mini that has been switched off since Tuesday.
    /// </remarks>
    public bool IsOnlineAt(DateTimeOffset now, TimeSpan? grace = null)
        => Status == RunnerAgentStatus.Online
           && LastHeartbeatAt is { } beat
           && beat + (grace ?? HeartbeatGrace) >= now.ToUniversalTime();

    /// <summary>
    /// Step 1 of section 33.3: an admin generates a single-use, short-TTL pairing token.
    /// </summary>
    /// <param name="pairingTokenHash">
    /// The verifier. The caller mints the token, shows it once, and never persists it.
    /// </param>
    public static RunnerAgent Invite(
        Guid orgId,
        string name,
        string pairingTokenHash,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingTokenHash);

        var createdAt = DomainTime.Resolve(now);
        var ttl = lifetime ?? DefaultPairingTokenLifetime;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl.Ticks);

        return new RunnerAgent(
            id ?? Guid.CreateVersion7(),
            orgId,
            name.Trim(),
            pairingTokenHash,
            createdAt + ttl,
            createdAt);
    }

    /// <summary>
    /// Step 2: the token is spent and the long-lived credential takes its place.
    /// </summary>
    /// <remarks>
    /// Clearing <see cref="PairingTokenHash"/> here is what makes the token single-use, and the
    /// entity's concurrency token is what makes it single-use under a race: two agents pairing with
    /// the same token in the same instant both read a live row, and exactly one of them saves.
    /// </remarks>
    public void CompletePairing(
        string credentialHash,
        string name,
        RunnerAgentMode mode,
        string agentVersion,
        int protocolVersion,
        int concurrency,
        RunnerAgentPlatform platform,
        IReadOnlyList<string> capabilities,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (Status == RunnerAgentStatus.Revoked)
        {
            throw new InvalidOperationException("A revoked agent cannot be paired again.");
        }

        var at = DomainTime.Resolve(now);

        CredentialHash = credentialHash;
        PairingTokenHash = null;
        PairingTokenExpiresAt = null;
        PairedAt = at;
        Name = name.Trim();
        Mode = mode;
        AgentVersion = agentVersion.Trim();
        ProtocolVersion = protocolVersion;
        Concurrency = Math.Max(1, concurrency);
        Status = RunnerAgentStatus.Offline;

        ApplyPlatform(platform);
        Capabilities = capabilities;
        CapabilitiesProbedAt = at;
    }

    /// <summary>The <c>hello</c> frame landed and a version was agreed (section 33.6).</summary>
    public void Connected(
        string name,
        RunnerAgentMode mode,
        string agentVersion,
        int protocolVersion,
        int concurrency,
        RunnerAgentPlatform platform,
        IReadOnlyList<string> capabilities,
        DateTimeOffset? capabilitiesProbedAt = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (Status == RunnerAgentStatus.Revoked)
        {
            throw new InvalidOperationException("A revoked agent cannot come online.");
        }

        var at = DomainTime.Resolve(now);

        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name.Trim();
        }

        Mode = mode;
        AgentVersion = agentVersion.Trim();
        ProtocolVersion = protocolVersion;
        Concurrency = Math.Max(1, concurrency);
        Status = RunnerAgentStatus.Online;
        LastHeartbeatAt = at;

        ApplyPlatform(platform);
        ReportCapabilities(capabilities, capabilitiesProbedAt ?? at, hash: null);
    }

    /// <summary>Section 32.2: a fresh probe replaces the set outright rather than merging into it.</summary>
    public void ReportCapabilities(
        IReadOnlyList<string> capabilities,
        DateTimeOffset? probedAt = null,
        string? hash = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        Capabilities = capabilities;
        CapabilitiesProbedAt = DomainTime.Resolve(probedAt);

        if (hash is not null)
        {
            CapabilitiesHash = hash;
        }
    }

    /// <summary>Liveness. Missing these is what marks an agent offline (section 33.3).</summary>
    public void Heartbeat(DateTimeOffset? now = null, string? capabilitiesHash = null)
    {
        if (Status == RunnerAgentStatus.Revoked)
        {
            return;
        }

        Status = RunnerAgentStatus.Online;
        LastHeartbeatAt = DomainTime.Resolve(now);

        if (capabilitiesHash is not null)
        {
            CapabilitiesHash = capabilitiesHash;
        }
    }

    /// <summary>The socket closed. Leases survive until they lapse; routing stops immediately.</summary>
    public void Disconnected()
    {
        if (Status == RunnerAgentStatus.Online)
        {
            Status = RunnerAgentStatus.Offline;
        }
    }

    /// <summary>
    /// Section 33.3: revocable instantly. The credential is destroyed here, not merely flagged.
    /// </summary>
    /// <remarks>
    /// Killing in-flight jobs and pushing the <c>revoked</c> frame are the connection's work; this is
    /// the half that survives a restart. Because the verifier is nulled rather than a boolean set, a
    /// build that forgot to check <see cref="IsRevoked"/> still cannot authenticate the old token.
    /// </remarks>
    public void Revoke(string reason, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = RunnerAgentStatus.Revoked;
        CredentialHash = null;
        PairingTokenHash = null;
        PairingTokenExpiresAt = null;
        RevokedAt = DomainTime.Resolve(now);
        RevokedReason = reason.Trim();
    }

    private void ApplyPlatform(RunnerAgentPlatform platform)
    {
        Os = platform.Os;
        Arch = platform.Arch;
        Rid = platform.Rid;
        Hostname = platform.Hostname;
        CpuCount = platform.CpuCount;
    }

    void IVersionedEntity.NextVersion() => Version++;
}
