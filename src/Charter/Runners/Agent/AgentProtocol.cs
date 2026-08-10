namespace Charter.Runners.Agent;

/// <summary>
/// The control plane's half of the wire contract with <c>charter-agent</c> (section 33.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This mirrors <c>Charter.Agent.Protocol.AgentProtocol</c> value for value.</strong> The two
/// assemblies do not reference each other on purpose — the daemon ships as a self-contained single
/// file (section 33.7) and must not carry EF Core, Serilog and the whole control plane with it — so
/// the contract exists twice and is pinned by <c>AgentPlaneProtocolTests</c>, which round-trips the
/// daemon's own types through this serialiser and back. If a constant here disagrees with the
/// daemon's, that test fails; it is the only thing standing between the two halves and a subtle
/// mismatch three sessions later, which is exactly what section 33.6 exists to prevent.
/// </para>
/// <para>
/// The agent dials out and the control plane never dials in (section 33.1). Everything below is
/// therefore reachable only over a socket the agent opened, plus the one HTTP call that trades a
/// pairing token for a long-lived credential.
/// </para>
/// </remarks>
public static class AgentProtocol
{
    /// <summary>The protocol version this control plane speaks.</summary>
    public const int Version = 1;

    /// <summary>The oldest version this control plane still understands.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>Sent on the pairing request and the WebSocket upgrade so a mismatch fails loudly.</summary>
    public const string VersionHeader = "Charter-Agent-Protocol";

    /// <summary>Pairing token exchange. One HTTP POST, once, before the socket is opened.</summary>
    public const string PairPath = "/api/agent/pair";

    /// <summary>The endpoint the agent's outbound WebSocket lands on.</summary>
    public const string ConnectPath = "/api/agent/connect";

    /// <summary>Query-string name carrying the protocol version on the upgrade request.</summary>
    public const string VersionQueryParameter = "protocol";

    /// <summary>Close code: the two sides could not agree a protocol version.</summary>
    public const int CloseProtocolMismatch = 4001;

    /// <summary>Close code: the agent credential was revoked or rejected (section 33.3).</summary>
    public const int CloseCredentialRevoked = 4003;

    /// <summary>Close code: another connection for the same agent took over.</summary>
    public const int CloseReplaced = 4008;

    /// <summary>Every version this control plane can speak, newest first.</summary>
    public static IReadOnlyList<int> SupportedVersions { get; } =
        [.. Enumerable.Range(MinimumSupportedVersion, Version - MinimumSupportedVersion + 1).Reverse()];

    public static bool Supports(int version) =>
        version >= MinimumSupportedVersion && version <= Version;

    /// <summary>
    /// Picks the newest version both sides can speak, or 0 when there is none.
    /// </summary>
    /// <param name="agentVersion">What the agent asked for in <c>hello</c>.</param>
    /// <param name="agentSupported">
    /// Everything the agent can speak. Present since protocol 1, so an agent one version ahead of the
    /// plane still connects on a version the plane knows rather than being refused outright.
    /// </param>
    public static int Negotiate(int agentVersion, IReadOnlyList<int>? agentSupported)
    {
        if (Supports(agentVersion))
        {
            return agentVersion;
        }

        if (agentSupported is null)
        {
            return 0;
        }

        var best = 0;
        foreach (var candidate in agentSupported)
        {
            if (Supports(candidate) && candidate > best)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The sentence a refused agent is shown. Section 33.6 asks for a clear message naming both
    /// versions and saying which side to upgrade, not a status code.
    /// </summary>
    public static string DescribeMismatch(int agentVersion)
    {
        var mine = string.Join(", ", SupportedVersions);

        var advice = agentVersion > Version
            ? "This agent is newer than the control plane. Upgrade the control plane, or run an agent "
              + $"build that still speaks protocol {Version}."
            : "The control plane is newer than this agent. Upgrade charter-agent to a build that speaks "
              + $"protocol {Version}.";

        return $"Protocol mismatch: charter-agent speaks {agentVersion}, this control plane speaks "
            + $"{mine}. {advice} No work will be granted until the versions agree.";
    }
}

/// <summary>Message type names. Both directions, one flat namespace, stable strings.</summary>
public static class MessageTypes
{
    // Agent -> control plane.

    /// <summary>First frame after the socket opens. Carries version, identity and capabilities.</summary>
    public const string Hello = "hello";

    /// <summary>Liveness plus lease renewal for every job the agent still holds (section 33.4).</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>A fresh capability probe, on restart and daily (section 32.2).</summary>
    public const string CapabilitiesReport = "capabilities.report";

    /// <summary>A request for up to <c>maxJobs</c> jobs this agent can actually run.</summary>
    public const string JobClaim = "job.claim";

    /// <summary>Progress and output for a running job, secrets scrubbed.</summary>
    public const string JobEvent = "job.event";

    /// <summary>Terminal report for a job the agent holds.</summary>
    public const string JobResult = "job.result";

    /// <summary>The agent is shutting down and is returning any leases it still holds.</summary>
    public const string Goodbye = "goodbye";

    // Control plane -> agent.

    /// <summary>Response to <see cref="Hello"/>. Carries the agreed version and the timing contract.</summary>
    public const string Welcome = "welcome";

    /// <summary>Response to <see cref="Hello"/> when no common version exists. Connection then closes.</summary>
    public const string ProtocolMismatch = "protocol.mismatch";

    /// <summary>Response to <see cref="Heartbeat"/>. Carries renewed lease expiries.</summary>
    public const string HeartbeatAck = "heartbeat.ack";

    /// <summary>Response to <see cref="JobClaim"/>. Zero or more jobs, each under a lease.</summary>
    public const string JobGrant = "job.grant";

    /// <summary>Stop a job the agent holds. Also sent on revocation.</summary>
    public const string JobCancel = "job.cancel";

    /// <summary>The agent credential was revoked. In-flight work stops and the socket closes.</summary>
    public const string Revoked = "revoked";

    /// <summary>A protocol-level complaint that is not fatal.</summary>
    public const string Error = "error";
}

/// <summary>The stable machine codes an <c>error</c> frame or an HTTP failure carries.</summary>
public static class AgentErrorCodes
{
    public const string PairingTokenRejected = "pairing_token_rejected";

    public const string PairingTokenExpired = "pairing_token_expired";

    public const string ProtocolUnsupported = "protocol_unsupported";

    public const string CredentialRevoked = "credential_revoked";

    public const string HandshakeRequired = "handshake_required";

    public const string MalformedFrame = "malformed_frame";

    public const string UnknownJob = "unknown_job";
}

/// <summary>How a job ended, in the words the wire uses. Mirrors the daemon's <c>JobOutcomes</c>.</summary>
public static class AgentJobOutcomes
{
    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>The agent gave the job back: lease lost, capability gone, or never runnable.</summary>
    public const string Abandoned = "abandoned";
}
