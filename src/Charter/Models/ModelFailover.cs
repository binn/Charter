namespace Charter.Models;

/// <summary>
/// What to do when the credential backing a running session is exhausted mid-flight. Section 20b.4.
/// </summary>
/// <remarks>
/// Never a silent model switch. A session that swaps models halfway produces incoherent work -
/// half the reasoning came from a different model with different conventions - so the choice is
/// between waiting and redoing the current step, and it is the repo's choice to make, not the
/// runtime's.
/// </remarks>
public enum ModelFailoverPolicy
{
    /// <summary>
    /// Checkpoint, pause, and resume on the same credential when its limit resets. The default.
    /// </summary>
    PauseAndResume,

    /// <summary>
    /// Checkpoint, then restart the current step from its beginning under the next credential in the
    /// chain, so the whole step comes from one model.
    /// </summary>
    RestartStep,
}

/// <summary>What the caller should do next.</summary>
public enum ModelFailoverAction
{
    /// <summary>
    /// No session is in flight, so switching is free and silent - section 20b.4's between-session
    /// failover. Use <see cref="ModelFailoverDecision.NextCredential"/>.
    /// </summary>
    UseNextCredential,

    /// <summary>
    /// Checkpoint and queue until <see cref="ModelFailoverDecision.ResumeAt"/>, then continue on the
    /// same credential and the same model.
    /// </summary>
    PauseUntilReset,

    /// <summary>
    /// Checkpoint and restart the current step under
    /// <see cref="ModelFailoverDecision.NextCredential"/>.
    /// </summary>
    RestartStepUnderNextCredential,
}

/// <summary>The plan for a mid-flight exhaustion.</summary>
/// <param name="Action">What to do.</param>
/// <param name="NextCredential">The credential to move to, where the action calls for one.</param>
/// <param name="ResumeAt">
/// When to resume, where the action is to wait. <see langword="null"/> means the provider gave no
/// reset, so the session queues indefinitely rather than guessing at a retry time.
/// </param>
public sealed record ModelFailoverDecision(
    ModelFailoverAction Action,
    ResolvedModelCredential? NextCredential = null,
    DateTimeOffset? ResumeAt = null);

/// <summary>
/// Turns an exhaustion plus a policy into a decision. Deliberately a pure function so the rule that
/// a session never changes model mid-flight is a testable property rather than a code review note.
/// </summary>
public static class ModelFailoverPlanner
{
    /// <summary>Decides what to do about an exhausted credential.</summary>
    /// <param name="policy">The repo's configured policy.</param>
    /// <param name="sessionInProgress">
    /// Whether work has already been produced by the exhausted credential in this session. When
    /// <see langword="false"/>, failover is between sessions and therefore free and silent.
    /// </param>
    /// <param name="nextResolution">What the chain offers next.</param>
    /// <param name="exhaustedUntil">
    /// When the exhausted credential recovers, from the provider's reset header.
    /// </param>
    public static ModelFailoverDecision Decide(
        ModelFailoverPolicy policy,
        bool sessionInProgress,
        ModelCredentialResolution nextResolution,
        DateTimeOffset? exhaustedUntil)
    {
        ArgumentNullException.ThrowIfNull(nextResolution);

        // Between sessions there is no coherence to protect: take whatever the chain offers.
        if (!sessionInProgress && nextResolution.Credential is not null)
        {
            return new ModelFailoverDecision(
                ModelFailoverAction.UseNextCredential,
                nextResolution.Credential);
        }

        var resumeAt = EarliestOf(exhaustedUntil, nextResolution.WaitingForCapacityUntil);

        if (!sessionInProgress)
        {
            // Nothing anywhere in the chain. Section 20b.3: queue as waiting for capacity, do not fail.
            return new ModelFailoverDecision(ModelFailoverAction.PauseUntilReset, null, resumeAt);
        }

        if (policy == ModelFailoverPolicy.RestartStep && nextResolution.Credential is not null)
        {
            return new ModelFailoverDecision(
                ModelFailoverAction.RestartStepUnderNextCredential,
                nextResolution.Credential);
        }

        // PauseAndResume, or RestartStep with nothing to restart under. Either way: wait, on the same
        // credential and the same model.
        return new ModelFailoverDecision(ModelFailoverAction.PauseUntilReset, null, exhaustedUntil ?? resumeAt);
    }

    private static DateTimeOffset? EarliestOf(DateTimeOffset? left, DateTimeOffset? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (var a, null) => a,
            (null, var b) => b,
            var (a, b) => a <= b ? a : b,
        };
}
