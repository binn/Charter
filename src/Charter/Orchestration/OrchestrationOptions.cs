using System.Security.Cryptography;
using System.Text;

namespace Charter.Orchestration;

/// <summary>
/// How the orchestrator and the dispatcher behave. Everything here has a working default.
/// </summary>
/// <remarks>
/// There is deliberately no environment variable for any of it. Section 4.2 is the complete list of
/// what an operator configures, and a poll interval is a tuning knob nobody should have to think
/// about — if a default here is wrong, the fix is to change the default.
/// </remarks>
public sealed class OrchestrationOptions
{
    /// <summary>
    /// The advisory lock key that keeps one dispatcher authoritative (section 2.3).
    /// </summary>
    /// <remarks>
    /// A constant derived from a fixed string, so every replica of the same instance competes for the
    /// same lock. Two Charters sharing one database — which nothing recommends — would also share it,
    /// which is the safe direction.
    /// </remarks>
    public long DispatcherLockKey { get; set; } = AdvisoryKey("charter.dispatcher");

    /// <summary>
    /// This process's identity in the queue, and what <c>ReleaseWorkerClaimsAsync</c> hands back on
    /// shutdown (section 31). Distinct per process, so two replicas never release each other's work.
    /// </summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>How long a claim survives without a heartbeat.</summary>
    public TimeSpan Lease { get; set; } = Domain.Job.DefaultLease;

    /// <summary>How often the dispatcher polls for claimable work.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a standby replica retries for the dispatcher lock.</summary>
    public TimeSpan LockRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the orchestrator reconciles sessions against their journals.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How many jobs to claim per round trip.</summary>
    public int BatchSize { get; set; } = 4;

    /// <summary>How long a failed dispatch waits before the queue offers it again.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wall-clock cap handed to a runner when the job does not name one (section 27.5).</summary>
    public int DefaultTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// The backend a new session is queued against, from <c>CHARTER_RUNNER</c> (section 2.2).
    /// </summary>
    /// <remarks>
    /// A preference, not a constraint: <see cref="Runners.IRunnerRegistry.RouteAsync"/> honours it
    /// when that backend can run the work and falls back to the first enabled one when it cannot, so
    /// an instance whose only runner changed between approval and dispatch still dispatches.
    /// </remarks>
    public Domain.RunnerKind DefaultRunner { get; set; } = Domain.RunnerKind.Agent;

    /// <summary>
    /// The model a new session runs on, from <c>CHARTER_MODEL_BUILD</c> (sections 4.2, 20b.1).
    /// </summary>
    /// <remarks>
    /// Recorded on the session at creation rather than read at dispatch, so an operator who changes
    /// the default does not silently change what an already-approved specification will be built by.
    /// </remarks>
    public string BuildModel { get; set; } = Configuration.ModelConfig.DefaultBuild;

    /// <summary>
    /// The instance's public URL, from <c>CHARTER_BASE_URL</c>. Callback and spec URLs are built from
    /// it, so a runner in a GitHub-hosted VM can reach the control plane.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("http://localhost:8080/");

    /// <summary>Where a session's callbacks land: <c>{base}/api/runners/sessions/{id}</c>.</summary>
    public Uri CallbackUrlFor(Guid sessionId)
        => new(BaseUrl, $"/api/runners/sessions/{sessionId:D}");

    /// <summary>Where the shim fetches the approved spec from (section 16).</summary>
    public Uri SpecUrlFor(Guid sessionId)
        => new(BaseUrl, $"/api/runners/sessions/{sessionId:D}/spec");

    /// <summary>Folds a name into the 64-bit key <c>pg_try_advisory_lock</c> takes.</summary>
    public static long AdvisoryKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return BitConverter.ToInt64(hash, 0);
    }
}
