using Charter.Domain;

namespace Charter.Runners;

/// <summary>
/// The path scope handed to a session (sections 7.3, 8).
/// </summary>
/// <remarks>
/// Carried on the dispatch so the shim can enforce it. The control plane sends it; it never relies on
/// it. Enforcement lives in <see cref="Charter.Runners.Shim.ShimPathScope"/>, inside the sandbox,
/// because a compromised session must not be able to widen its own scope by talking to the UI.
/// </remarks>
public sealed record RunnerPathScope(IReadOnlyList<string> Allow, IReadOnlyList<string> Deny)
{
    /// <summary>Unrestricted — an allow list of <c>**</c> with nothing denied.</summary>
    public static RunnerPathScope Unrestricted { get; } = new([], []);
}

/// <summary>Everything a backend needs to start one session, and nothing more.</summary>
/// <param name="SessionId">Correlates every event, span and log line for this run (section 19).</param>
/// <param name="RepoFullName"><c>owner/name</c>, as GitHub spells it.</param>
/// <param name="BaseBranch">The branch the session branches from.</param>
/// <param name="BaseCommitSha">Recorded per session so section 17 can compute staleness.</param>
/// <param name="AdapterId">The agent adapter to invoke (section 12b).</param>
/// <param name="Model">The provider-qualified build model (section 20b.1).</param>
/// <param name="RunnerImage">From the repo's <c>.charter/config.yml</c> (sections 8, 32.1).</param>
/// <param name="CallbackUrl">
/// The base the sandbox calls back on. <c>{callback}/credentials</c>, <c>{callback}/events</c> and
/// <c>{callback}/result</c> must all resolve — the shipped workflow calls all three.
/// </param>
/// <param name="SpecUrl">Where the shim fetches the approved spec from (section 16).</param>
/// <param name="PathScope">Enforced in the runner, never in the UI (section 7.3).</param>
/// <param name="RequiredCapabilities">What this session needs from a runner (section 27.3).</param>
/// <param name="TimeoutMinutes">Wall-clock cap. Section 27.5: builds are minutes to an hour.</param>
/// <param name="DispatchKey">
/// Stable for a session across control-plane restarts. A backend that can deduplicate uses it; the
/// orchestrator uses it as the idempotency key of the dispatch event, which is what makes
/// re-dispatch after a restart structurally impossible (section 2.3).
/// </param>
public sealed record RunnerDispatch(
    Guid SessionId,
    string RepoFullName,
    string BaseBranch,
    string BaseCommitSha,
    string AdapterId,
    string Model,
    string? RunnerImage,
    Uri CallbackUrl,
    Uri SpecUrl,
    RunnerPathScope PathScope,
    IReadOnlyList<string> RequiredCapabilities,
    int TimeoutMinutes,
    string DispatchKey);

/// <summary>What a backend reports back from a dispatch.</summary>
/// <param name="Accepted">False when the backend refused; <paramref name="Explanation"/> says why.</param>
/// <param name="ExternalReference">
/// The backend's own handle — a workflow run URL, a container id, an agent claim id. Persisted on the
/// dispatch event so a restarted control plane can still cancel the run it never started.
/// </param>
/// <param name="Explanation">Plain language, shown to an engineer. Never a stack trace.</param>
public sealed record RunnerDispatchResult(bool Accepted, string? ExternalReference, string? Explanation)
{
    public static RunnerDispatchResult Ok(string? externalReference = null)
        => new(true, externalReference, null);

    public static RunnerDispatchResult Refused(string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        return new RunnerDispatchResult(false, null, explanation);
    }
}

/// <summary>A request to stop a run that is already out there (section 11).</summary>
public sealed record RunnerCancellation(Guid SessionId, string? ExternalReference, string Reason);

/// <summary>
/// What cancelling achieved.
/// </summary>
/// <param name="Stopped">
/// True when the backend confirmed the run is dead. False is not a failure: a GitHub Actions job that
/// already finished cannot be cancelled, and the session still settles.
/// </param>
public sealed record RunnerCancelResult(bool Stopped, string? Explanation)
{
    public static RunnerCancelResult Confirmed { get; } = new(true, null);

    public static RunnerCancelResult NothingToStop(string explanation) => new(false, explanation);
}

/// <summary>What a backend is and what it can run (sections 27.3, 32.2).</summary>
/// <param name="Kind">Which of the three backends of section 2.2 this is.</param>
/// <param name="Name">Human-readable, for the queued-with-no-runner explanation and the UI.</param>
/// <param name="Capabilities">
/// Already expanded by <see cref="RunnerCapability.ExpandAll"/>, so matching is set containment.
/// </param>
/// <param name="Online">
/// False for a registered agent that has stopped heartbeating (section 33.3). An offline runner never
/// routes, but it still explains itself, because "your Mac mini is offline" is a far better message
/// than "no runner has macOS".
/// </param>
public sealed record RunnerDescriptor(
    RunnerKind Kind,
    string Name,
    IReadOnlyList<string> Capabilities,
    bool Online = true)
{
    /// <summary>True when this runner advertises everything <paramref name="required"/> asks for.</summary>
    public bool CanRun(IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        var advertised = Capabilities as IReadOnlyCollection<string> ?? [.. Capabilities];
        return RunnerCapability.Missing(advertised, required).Count == 0;
    }
}

/// <summary>
/// The execution seam of section 2.1: everything the control plane knows about running an agent.
/// </summary>
/// <remarks>
/// <para>
/// An implementation <em>dispatches</em> and <em>cancels</em>. It never holds orchestration state,
/// never decides what happens next, and never keeps a session in a field: the container can restart
/// mid-session, so anything an implementation remembered would be lost (section 2.3). Everything an
/// implementation learns about a run comes back as events written to Postgres, and the orchestrator
/// reads them from there — including, after a restart, events for a run it never started.
/// </para>
/// <para>
/// Section 3.1 is emphatic that the implementations here only ever dispatch. The work itself is done
/// by <c>Charter.DetachedRunner</c>, the shim, which all three backends invoke.
/// </para>
/// </remarks>
public interface IAgentRunner
{
    /// <summary>Which backend this is. Unique within a registry.</summary>
    RunnerKind Kind { get; }

    /// <summary>
    /// What this backend advertises right now. Re-read rather than cached: section 32.2 requires a
    /// runner that picked up an Xcode update to stop advertising the old version.
    /// </summary>
    ValueTask<RunnerDescriptor> DescribeAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts one session. Must be safe to call twice with the same <c>DispatchKey</c>.</summary>
    Task<RunnerDispatchResult> DispatchAsync(RunnerDispatch dispatch, CancellationToken cancellationToken = default);

    /// <summary>Kills a run. Section 11: the cancel button must actually stop the runner.</summary>
    Task<RunnerCancelResult> CancelAsync(RunnerCancellation cancellation, CancellationToken cancellationToken = default);
}
