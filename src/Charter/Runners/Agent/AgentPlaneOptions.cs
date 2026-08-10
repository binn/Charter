namespace Charter.Runners.Agent;

/// <summary>
/// The timing contract the control plane hands every agent in <c>welcome</c> (section 33.6).
/// </summary>
/// <remarks>
/// The plane owns these numbers, not the agent: an agent that decided its own lease TTL could keep a
/// job alive past the point the queue had given up on it, and two runners would push to one branch.
/// As with <c>OrchestrationOptions</c> there is deliberately no environment variable for any of it —
/// section 4.2 is the complete list of what an operator configures, and if a default here is wrong
/// the fix is to change the default.
/// </remarks>
public sealed class AgentPlaneOptions
{
    /// <summary>How often an agent must heartbeat. Also how often it renews every lease it holds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lease TTL granted with each claim (section 33.4). Comfortably several heartbeats, so a lease
    /// only lapses when the agent has genuinely stopped rather than when one frame was lost.
    /// </summary>
    public TimeSpan Lease { get; set; } = Domain.Job.DefaultLease;

    /// <summary>Section 32.2: a Mac mini that got an Xcode update must not advertise the old one.</summary>
    public TimeSpan ReprobeInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Minimum gap between claims, so an idle agent does not poll the queue flat out.</summary>
    public TimeSpan ClaimInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a socket may stay open without a <c>hello</c>. A connection that authenticated and
    /// then said nothing is either a probe or a broken client; neither deserves a slot.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a connection may go silent before the plane closes it.
    /// </summary>
    /// <remarks>
    /// Several heartbeat intervals. Closing is safe because it costs the agent only a reconnect: its
    /// leases live in Postgres and it reports what it still holds in the next <c>hello</c>.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Ceiling on one <c>job.claim</c>, whatever the agent's concurrency says.</summary>
    public int MaxJobsPerClaim { get; set; } = 8;

    /// <summary>What an empty <c>job.grant</c> suggests waiting before asking again.</summary>
    public int EmptyClaimRetryAfterSeconds { get; set; } = 15;

    /// <summary>
    /// How long the per-job version-control token is good for (sections 7.4, 33.5).
    /// </summary>
    /// <remarks>
    /// GitHub installation tokens expire an hour after issue and the broker does not report the
    /// instant, so this is what the agent is told. Being slightly pessimistic is the safe direction:
    /// an agent that refreshes early costs a round trip, one that refreshes late fails a push.
    /// </remarks>
    public TimeSpan RepositoryTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The shim the agent invokes, as it is named on the agent host (section 3.1).</summary>
    public string ShimExecutable { get; set; } = "charter-runner-shim";

    /// <summary>The default container image for docker-mode agents (section 32.1).</summary>
    public string DefaultRunnerImage { get; set; } = GitHubActionsRunnerOptions.DefaultRunnerImage;

    /// <summary>Wall-clock cap handed to an agent when the job does not name one (section 27.5).</summary>
    public int DefaultTimeoutMinutes { get; set; } = 60;

    /// <summary>Section 33.6: offered, never installed. Null when this instance publishes no build.</summary>
    public AgentUpdateOffer? UpdateOffer { get; set; }

    public int HeartbeatSeconds => (int)Math.Round(HeartbeatInterval.TotalSeconds);

    public int LeaseSeconds => (int)Math.Round(Lease.TotalSeconds);

    public int ReprobeSeconds => (int)Math.Round(ReprobeInterval.TotalSeconds);

    public int ClaimIntervalSeconds => (int)Math.Round(ClaimInterval.TotalSeconds);
}
