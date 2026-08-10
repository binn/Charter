namespace Charter.Agent.Protocol;

/// <summary>
/// The wire contract between <c>charter-agent</c> and the control plane (section 33.6).
/// </summary>
/// <remarks>
/// The agent dials out; the control plane never dials in (section 33.1). Everything here therefore
/// describes what the agent sends and what it expects back over a single outbound WebSocket, plus
/// the one HTTP call that trades a pairing token for a long-lived credential.
/// </remarks>
public static class AgentProtocol
{
    /// <summary>The protocol version this build speaks.</summary>
    public const int Version = 1;

    /// <summary>The oldest version this build still understands.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>Sent on the pairing request and the WebSocket upgrade so a mismatch fails loudly.</summary>
    public const string VersionHeader = "Charter-Agent-Protocol";

    /// <summary>Pairing token exchange. One HTTP POST, once, before the socket is opened.</summary>
    public const string PairPath = "/api/agent/pair";

    /// <summary>The outbound WebSocket endpoint.</summary>
    public const string ConnectPath = "/api/agent/connect";

    /// <summary>Query-string name carrying the protocol version on the upgrade request.</summary>
    public const string VersionQueryParameter = "protocol";

    /// <summary>Close code: the two sides could not agree a protocol version.</summary>
    public const int CloseProtocolMismatch = 4001;

    /// <summary>Close code: the agent credential was revoked or rejected (section 33.3).</summary>
    public const int CloseCredentialRevoked = 4003;

    /// <summary>Close code: another connection for the same agent took over.</summary>
    public const int CloseReplaced = 4008;

    /// <summary>Every version this build can speak, newest first.</summary>
    public static IReadOnlyList<int> SupportedVersions { get; } =
        [.. Enumerable.Range(MinimumSupportedVersion, Version - MinimumSupportedVersion + 1).Reverse()];

    public static bool Supports(int version) =>
        version >= MinimumSupportedVersion && version <= Version;
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
