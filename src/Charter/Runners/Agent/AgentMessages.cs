using System.Text.Json;

namespace Charter.Runners.Agent;

// -------------------------------------------------------------------------------------------------
// Pairing (HTTP, once) — section 33.3 steps 1 and 2.
// -------------------------------------------------------------------------------------------------

/// <summary>POST body for <see cref="AgentProtocol.PairPath"/>.</summary>
/// <remarks>
/// Every property here is written by <c>Charter.Agent.Pairing.HttpPairingClient</c>. The pairing
/// token is single-use and short-TTL; it is spent here and never written to disk on either side.
/// </remarks>
public sealed record PairRequest
{
    public required string PairingToken { get; init; }

    public required string Name { get; init; }

    /// <summary><c>docker</c> or <c>native</c>.</summary>
    public required string Mode { get; init; }

    public required string AgentVersion { get; init; }

    public required int ProtocolVersion { get; init; }

    public required int Concurrency { get; init; }

    public required HostPlatform Platform { get; init; }

    /// <summary>Probed, never declared (section 32.2).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>What the host is, as facts the agent can establish about itself.</summary>
public sealed record HostPlatform
{
    /// <summary><c>linux</c>, <c>macos</c> or <c>windows</c>.</summary>
    public required string Os { get; init; }

    public required string Arch { get; init; }

    /// <summary>.NET runtime identifier, e.g. <c>osx-arm64</c>. Used to offer the right update asset.</summary>
    public required string Rid { get; init; }

    public required string Hostname { get; init; }

    public required int CpuCount { get; init; }

    public long? TotalMemoryMb { get; init; }
}

/// <summary>Success body from <see cref="AgentProtocol.PairPath"/>.</summary>
public sealed record PairResponse
{
    public required string AgentId { get; init; }

    /// <summary>
    /// The long-lived agent credential. Returned exactly once, stored 0600 on the agent host, and
    /// held here only as a PBKDF2 verifier (see <c>RunnerAgent.CredentialHash</c>).
    /// </summary>
    public required string AgentToken { get; init; }

    public required int ProtocolVersion { get; init; }

    /// <summary>Never let a record printer put the credential into an interpolated string.</summary>
    public override string ToString() => $"PairResponse {{ AgentId = {AgentId}, AgentToken = redacted }}";
}

/// <summary>Failure body from any control-plane HTTP call. <c>error</c> is a stable machine code.</summary>
public sealed record ErrorResponse
{
    public string? Error { get; init; }

    public string? Message { get; init; }
}

// -------------------------------------------------------------------------------------------------
// Handshake — section 33.6.
// -------------------------------------------------------------------------------------------------

/// <summary>First frame the agent sends after the socket opens.</summary>
public sealed record HelloPayload
{
    public required int ProtocolVersion { get; init; }

    /// <summary>Every version the agent can speak, newest first. Lets the plane pick a common one.</summary>
    public required IReadOnlyList<int> SupportedProtocolVersions { get; init; }

    public required string AgentVersion { get; init; }

    public required string Name { get; init; }

    /// <summary><c>docker</c> or <c>native</c>.</summary>
    public required string Mode { get; init; }

    public required int Concurrency { get; init; }

    public required HostPlatform Platform { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required DateTimeOffset CapabilitiesProbedAt { get; init; }

    /// <summary>Jobs this agent still holds from a previous connection, so leases survive a reconnect.</summary>
    public IReadOnlyList<string> HeldJobIds { get; init; } = [];
}

/// <summary>The control plane's answer to <see cref="HelloPayload"/>.</summary>
public sealed record WelcomePayload
{
    public required string AgentId { get; init; }

    /// <summary>The version the plane has chosen for this connection.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Every version the plane can speak. Informational, for the agent's error text.</summary>
    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = [];

    public string? ServerVersion { get; init; }

    /// <summary>How often the agent must heartbeat. The plane owns this number.</summary>
    public required int HeartbeatSeconds { get; init; }

    /// <summary>Lease TTL granted with each claim, renewed by heartbeat (section 33.4).</summary>
    public required int LeaseSeconds { get; init; }

    /// <summary>How often to re-probe capabilities. Daily by default (section 32.2).</summary>
    public int ReprobeSeconds { get; init; } = 86_400;

    /// <summary>Minimum gap between claim attempts, so an idle agent does not hammer the queue.</summary>
    public int ClaimIntervalSeconds { get; init; } = 5;

    /// <summary>Set when a newer agent build exists (section 33.6). Acted on only if opted in.</summary>
    public AgentUpdateOffer? Update { get; init; }
}

/// <summary>An available agent build. The default is to warn, not to install.</summary>
public sealed record AgentUpdateOffer
{
    public required string LatestVersion { get; init; }

    public string? DownloadUrl { get; init; }

    /// <summary>Hex SHA-256 of the asset. An update is never applied without verifying this.</summary>
    public string? Sha256 { get; init; }

    public string? ReleaseNotesUrl { get; init; }
}

/// <summary>Sent instead of <see cref="WelcomePayload"/> when there is no version in common.</summary>
public sealed record ProtocolMismatchPayload
{
    public required int ServerProtocolVersion { get; init; }

    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = [];

    public string? Message { get; init; }
}

// -------------------------------------------------------------------------------------------------
// Heartbeat and capabilities — sections 33.3, 32.2.
// -------------------------------------------------------------------------------------------------

public sealed record HeartbeatPayload
{
    /// <summary><c>ready</c>, <c>busy</c>, <c>draining</c> or <c>refusing</c> (protocol mismatch).</summary>
    public required string Status { get; init; }

    /// <summary>Every job the agent still holds. Renewing these is the point of the heartbeat.</summary>
    public required IReadOnlyList<string> HeldJobIds { get; init; }

    public required int AvailableSlots { get; init; }

    /// <summary>Stable hash of the advertised capability set, so the plane can spot drift cheaply.</summary>
    public required string CapabilitiesHash { get; init; }
}

public sealed record HeartbeatAckPayload
{
    /// <summary>Renewed lease expiries, per job. A job absent here has lost its lease.</summary>
    public IReadOnlyList<LeaseGrant> Leases { get; init; } = [];

    /// <summary>Set when the plane wants a capability probe now rather than at the next daily tick.</summary>
    public bool ReprobeRequested { get; init; }
}

public sealed record LeaseGrant
{
    public required string JobId { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }
}

public sealed record CapabilitiesReportPayload
{
    public required IReadOnlyList<string> Capabilities { get; init; }

    public required DateTimeOffset ProbedAt { get; init; }

    public required string CapabilitiesHash { get; init; }

    /// <summary>Per-probe detail: what ran, whether the tool was present, what was parsed out.</summary>
    public IReadOnlyList<ProbeReport> Probes { get; init; } = [];
}

public sealed record ProbeReport
{
    public required string Name { get; init; }

    public required bool ToolPresent { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Why a probe produced nothing. Never contains command output verbatim.</summary>
    public string? Note { get; init; }
}

// -------------------------------------------------------------------------------------------------
// Job claiming — section 33.4.
// -------------------------------------------------------------------------------------------------

/// <summary>The agent asks; the control plane never pushes.</summary>
public sealed record JobClaimPayload
{
    /// <summary>Never more than the agent's remaining concurrency slots.</summary>
    public required int MaxJobs { get; init; }

    /// <summary>Repeated on every claim so the plane filters without a stale registration read.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    public required string Mode { get; init; }
}

/// <summary>Zero or more jobs, correlated to the claim that asked for them.</summary>
public sealed record JobGrantPayload
{
    public IReadOnlyList<JobAssignment> Jobs { get; init; } = [];

    /// <summary>Optional hint: seconds until it is worth asking again when the queue was empty.</summary>
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>One unit of work, under a lease.</summary>
public sealed record JobAssignment
{
    public required string JobId { get; init; }

    /// <summary>Matches the control plane's <c>JobType</c>: <c>recon</c>, <c>build</c>, <c>recap</c>.</summary>
    public required string Type { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }

    public int Attempt { get; init; } = 1;

    public int MaxAttempts { get; init; } = 3;

    /// <summary>What the plane matched against. The agent re-checks locally before accepting.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    public JobRepo? Repo { get; init; }

    /// <summary>Container image for docker mode. Ignored in native mode.</summary>
    public string? RunnerImage { get; init; }

    public required JobCommand Command { get; init; }

    /// <summary>Per-job, short-TTL, single-repo. Never logged, never persisted (section 33.5).</summary>
    public JobSecrets? Secrets { get; init; }

    /// <summary>Opaque control-plane payload, forwarded to the runner shim untouched.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Wall-clock cap. Section 27.5 requires one alongside the token cap.</summary>
    public int? TimeoutSeconds { get; init; }
}

public sealed record JobRepo
{
    /// <summary><c>owner/name</c>.</summary>
    public required string FullName { get; init; }

    public required string CloneUrl { get; init; }

    public string? DefaultBranch { get; init; }

    /// <summary>The working branch for this session.</summary>
    public string? Branch { get; init; }

    /// <summary>Cache and git-mirror scope. Always the repository — never shared (section 32.3).</summary>
    public string? CacheScope { get; init; }
}

public sealed record JobCommand
{
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Non-secret environment. Secrets arrive in <see cref="JobSecrets"/> and are merged late.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Relative to the job's scoped working directory. Never absolute.</summary>
    public string? WorkingSubdirectory { get; init; }
}

/// <summary>
/// The only credentials that ever cross the wire (section 33.5, section 7.4).
/// </summary>
/// <remarks>
/// A version-control installation token scoped to the one repository in this job, and a scoped model
/// credential. Never a refresh token, never the control plane's environment, never a credential for
/// another repository. Signing identities, licences and registry tokens are operator-provisioned on
/// the agent host and never transmitted (section 32.8).
/// <para>
/// <see cref="ToString"/> is overridden on every type here because the default record printer would
/// put a live secret into any interpolated string that touched one — including a log line written by
/// somebody who had no idea they were holding a credential.
/// </para>
/// </remarks>
public sealed record JobSecrets
{
    public GitHubInstallationToken? GitHub { get; init; }

    public ModelCredential? Model { get; init; }

    /// <summary>Every secret value in this envelope, for scrubbing and for building child env.</summary>
    public IEnumerable<string> Values()
    {
        if (!string.IsNullOrEmpty(GitHub?.Token))
        {
            yield return GitHub.Token;
        }

        if (!string.IsNullOrEmpty(Model?.ApiKey))
        {
            yield return Model.ApiKey;
        }
    }

    public override string ToString() => "JobSecrets { redacted }";
}

public sealed record GitHubInstallationToken
{
    public required string Token { get; init; }

    /// <summary>The single repository this token can see. Anything else is a control-plane bug.</summary>
    public required string Repository { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => "GitHubInstallationToken { redacted }";
}

public sealed record ModelCredential
{
    /// <summary><c>anthropic</c>, <c>openai</c>, <c>openrouter</c>, and so on (section 20b).</summary>
    public required string Provider { get; init; }

    public required string ApiKey { get; init; }

    public string? BaseUrl { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public override string ToString() => "ModelCredential { redacted }";
}

/// <summary>Streamed progress for a running job. Output is scrubbed by the agent before it is sent.</summary>
public sealed record JobEventPayload
{
    public required string JobId { get; init; }

    /// <summary>Monotonic per job, so the plane can order and de-duplicate on reconnect.</summary>
    public required long Sequence { get; init; }

    /// <summary><c>started</c>, <c>stdout</c>, <c>stderr</c>, <c>note</c>.</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>Terminal report. The lease is released by the plane on receipt.</summary>
public sealed record JobResultPayload
{
    public required string JobId { get; init; }

    /// <summary><c>succeeded</c>, <c>failed</c>, <c>cancelled</c> or <c>abandoned</c> (lease lost).</summary>
    public required string Outcome { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Scrubbed. Terse enough to show in the UI without a transcript.</summary>
    public string? Error { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }

    public long DurationMs { get; init; }
}

public sealed record JobCancelPayload
{
    public required string JobId { get; init; }

    public string? Reason { get; init; }
}

public sealed record RevokedPayload
{
    public string? Reason { get; init; }
}

public sealed record ErrorPayload
{
    public string? Code { get; init; }

    public string? Message { get; init; }
}

public sealed record GoodbyePayload
{
    public string? Reason { get; init; }

    public IReadOnlyList<string> ReleasedJobIds { get; init; } = [];
}
