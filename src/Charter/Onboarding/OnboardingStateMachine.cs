using Charter.Domain;

namespace Charter.Onboarding;

/// <summary>What happened, that onboarding might react to.</summary>
public enum OnboardingSignal
{
    /// <summary>An engineer asked for the read-only recon run to start.</summary>
    StartRecon,

    /// <summary>Recon finished and produced a proposal.</summary>
    ReconCompleted,

    /// <summary>Recon failed. The repository stays where it was.</summary>
    ReconFailed,

    /// <summary>The scope config was confirmed and its pull request opened.</summary>
    ScopeProposed,

    /// <summary>The smoke test passed.</summary>
    SmokeTestPassed,

    /// <summary>The smoke test failed. Retryable, so the repository stays in <c>smoke_test</c>.</summary>
    SmokeTestFailed,

    /// <summary>An engineer asked for recon to run again — repos drift (section 9).</summary>
    ReRecon,

    /// <summary>An admin disabled the repository.</summary>
    Disable,

    /// <summary>An admin re-enabled it. It re-earns readiness rather than inheriting it.</summary>
    Enable,
}

/// <summary>The outcome of asking the state machine what a signal means.</summary>
/// <param name="Allowed">Whether the transition is legal.</param>
/// <param name="Status">The status afterwards. The current one when nothing moves.</param>
/// <param name="Explanation">Why, in one line.</param>
public sealed record OnboardingTransition(bool Allowed, RepoStatus Status, string Explanation)
{
    /// <summary>Whether the status actually changed.</summary>
    public bool Moved(RepoStatus from) => Allowed && Status != from;
}

/// <summary>
/// The onboarding state machine of section 9: pending, recon, configuring, smoke test, ready.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over the current status and one signal — no database, no clock, no services — so
/// the rules can be read and tested without a world around them, and so there is exactly one place
/// that knows a repository cannot skip from <c>pending</c> to <c>ready</c>.
/// </para>
/// <para>
/// Two rules here are load-bearing rather than bookkeeping.
/// </para>
/// <para>
/// <strong>Readiness is only ever earned by a passing smoke test.</strong> There is no signal that
/// moves a repository to <see cref="RepoStatus.Ready"/> other than
/// <see cref="OnboardingSignal.SmokeTestPassed"/>, and no path that reaches it from anywhere but
/// <see cref="RepoStatus.SmokeTest"/>. Section 9 says readiness is earned; an administrative override
/// would make that a convention rather than a property.
/// </para>
/// <para>
/// <strong>Re-recon does not un-ready a working repository.</strong> Section 9 offers re-recon on
/// demand because repos drift, and a drifting repo is still a working one. Taking a ready repository
/// back to <c>recon</c> would make every requester's project vanish from their list mid-afternoon, so
/// re-recon on a ready repository runs alongside it and leaves the status alone. Before readiness,
/// re-recon does move the status, because there is nothing to protect.
/// </para>
/// </remarks>
public static class OnboardingStateMachine
{
    /// <summary>What <paramref name="signal"/> means for a repository in <paramref name="current"/>.</summary>
    public static OnboardingTransition Next(RepoStatus current, OnboardingSignal signal)
    {
        // Disabled is a full stop. Nothing but an admin re-enabling moves it, and re-enabling does
        // not restore readiness: section 7.3's deny-by-default applies to a repository coming back
        // just as it did the first time.
        if (current == RepoStatus.Disabled)
        {
            return signal == OnboardingSignal.Enable
                ? new OnboardingTransition(true, RepoStatus.Pending, "re-enabled; readiness must be re-earned")
                : Refuse(current, "the repository is disabled");
        }

        return signal switch
        {
            OnboardingSignal.Disable =>
                new OnboardingTransition(true, RepoStatus.Disabled, "disabled by an admin"),

            OnboardingSignal.Enable =>
                Refuse(current, "the repository is not disabled"),

            OnboardingSignal.StartRecon => current == RepoStatus.Pending
                ? new OnboardingTransition(true, RepoStatus.Recon, "recon started")
                : Refuse(current, "recon starts from pending; use re-recon afterwards"),

            OnboardingSignal.ReconCompleted => current == RepoStatus.Recon
                ? new OnboardingTransition(true, RepoStatus.Configuring, "recon finished; scope awaits confirmation")
                : Refuse(current, "no recon run is in flight"),

            OnboardingSignal.ReconFailed => current == RepoStatus.Recon
                ? new OnboardingTransition(true, RepoStatus.Pending, "recon failed; back to pending to retry")
                : Refuse(current, "no recon run is in flight"),

            OnboardingSignal.ScopeProposed => current == RepoStatus.Configuring
                ? new OnboardingTransition(true, RepoStatus.SmokeTest, "scope config proposed; smoke test next")
                : Refuse(current, "scope is confirmed from configuring"),

            OnboardingSignal.SmokeTestPassed => current == RepoStatus.SmokeTest
                ? new OnboardingTransition(true, RepoStatus.Ready, "smoke test passed; the repository is requestable")
                : Refuse(
                    current,
                    "readiness is earned by a passing smoke test and cannot be reached any other way"),

            // Retryable in place. Dropping back to configuring would discard a scope config that was
            // fine, on the evidence of a preview environment that was not.
            OnboardingSignal.SmokeTestFailed => current == RepoStatus.SmokeTest
                ? new OnboardingTransition(true, RepoStatus.SmokeTest, "smoke test failed; retry when it is fixed")
                : Refuse(current, "no smoke test is in flight"),

            OnboardingSignal.ReRecon => current switch
            {
                RepoStatus.Ready => new OnboardingTransition(
                    true,
                    RepoStatus.Ready,
                    "re-recon runs alongside a ready repository; requesters keep working"),
                RepoStatus.Recon => Refuse(current, "recon is already running"),
                _ => new OnboardingTransition(true, RepoStatus.Recon, "re-running recon"),
            },

            _ => Refuse(current, "unrecognised signal"),
        };
    }

    /// <summary>Whether requesters may see this repository at all (section 9).</summary>
    public static bool IsRequesterVisible(RepoStatus status) => status == RepoStatus.Ready;

    private static OnboardingTransition Refuse(RepoStatus current, string why)
        => new(false, current, why);
}
