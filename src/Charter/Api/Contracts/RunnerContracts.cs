namespace Charter.Api.Contracts;

/// <summary>
/// One capability a Charter Agent reported about its own host (sections 27.3, 32.2).
/// </summary>
/// <remarks>
/// Section 32.2: a runner <strong>probes and reports</strong> rather than being told what it has.
/// <see cref="ProbedBy"/> is the command that found it, which is the difference between a claim and a
/// measurement — and it is what lets an engineer answer <em>"why does this agent think it has Xcode
/// 16.2"</em>. It is absent when this build does not know which command produces that family, because
/// naming a plausible command that was never run would be the opposite of the point.
/// </remarks>
public sealed record AgentCapabilityResponse
{
    /// <summary>The matchable identifier a session's requirements are checked against: <c>xcode:16.2</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The family it groups under: <c>xcode</c>, <c>dotnet</c>, <c>usb_device</c>, <c>os</c>.</summary>
    public required string Family { get; init; }

    /// <summary>Human label: <em>.NET SDK</em>, <em>USB device</em>.</summary>
    public required string Label { get; init; }

    public string? Version { get; init; }

    /// <summary>The command the agent ran to find it (section 32.2).</summary>
    public string? ProbedBy { get; init; }

    public required DateTimeOffset ProbedAt { get; init; }
}

/// <summary>Section 33.4: a concurrency limit per agent, defaulting conservatively.</summary>
public sealed record AgentConcurrencyResponse
{
    public required int Limit { get; init; }

    /// <summary>Jobs this agent holds a live lease on right now.</summary>
    public required int InFlight { get; init; }
}

/// <summary>One registered Charter Agent (section 33.3).</summary>
public sealed record RunnerAgentResponse
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ApiAgentMode Mode { get; init; }

    /// <summary>The <c>charter-agent</c> build that last connected, e.g. <c>0.4.1</c>.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Section 33.6: agent and control plane negotiate a protocol version on connect. False means it
    /// has refused to claim work — a clear message now beats subtle failures three sessions later.
    /// </summary>
    public required bool ProtocolCompatible { get; init; }

    public string? ProtocolNote { get; init; }

    public required ApiAgentStatus Status { get; init; }

    /// <summary>Section 33.4: missed heartbeats mark it offline and its in-flight jobs are re-queued.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    public required IReadOnlyList<AgentCapabilityResponse> Capabilities { get; init; }

    public required AgentConcurrencyResponse Concurrency { get; init; }

    public required string Os { get; init; }

    public required string Arch { get; init; }
}

/// <summary>
/// Section 27.3. A session with no eligible runner <strong>queues with a clear explanation</strong>
/// rather than failing, and this is that explanation's data.
/// </summary>
public sealed record QueuedSessionDemandResponse
{
    public required string RequestId { get; init; }

    public required string Title { get; init; }

    /// <summary>What the session requires: <c>["macos", "xcode:16"]</c>.</summary>
    public required IReadOnlyList<string> Requires { get; init; }

    /// <summary>
    /// Computed here, never in the client. Empty means nothing on this instance can run it.
    /// </summary>
    /// <remarks>
    /// The UI renders the reasoning; the server owns the verdict. Capability matching is set
    /// containment over the expanded advertisement (<c>RunnerCapability.ExpandAll</c>), which is the
    /// same test the job queue's Postgres filter applies — so an agent listed here is an agent that
    /// could actually claim the job.
    /// </remarks>
    public required IReadOnlyList<string> EligibleAgentIds { get; init; }

    /// <summary>Plain language, already written server-side. Rendered verbatim.</summary>
    public string? QueuedReason { get; init; }
}

/// <summary><c>GET /api/runners</c> (sections 33.3, 27.3).</summary>
public sealed record RunnersViewResponse
{
    public required IReadOnlyList<RunnerAgentResponse> Agents { get; init; }

    /// <summary>Sessions waiting on a runner right now, with what each one needs.</summary>
    public required IReadOnlyList<QueuedSessionDemandResponse> Waiting { get; init; }
}

/// <summary>
/// Section 33.3 step 1. Single-use, short-TTL, and shown exactly once.
/// </summary>
/// <remarks>
/// There is deliberately no endpoint that reads a pairing token back: what the row holds is a
/// verifier, not the token, so "show it again" is not a feature that was left out — it is one the
/// data model cannot offer.
/// </remarks>
public sealed record PairingTokenResponse
{
    public required string Token { get; init; }

    /// <summary>
    /// The exact command to run, assembled here so <c>--server</c> carries the instance's real base
    /// URL rather than whatever origin the browser happens to be pointed at.
    /// </summary>
    public required string Command { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
