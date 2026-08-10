using Charter.Models;

namespace Charter.Refinement;

/// <summary>Why refinement refused.</summary>
public enum RefusalReason
{
    /// <summary>The change would touch paths the repo denies (sections 7.3, 8, 10).</summary>
    DeniedPaths,

    /// <summary>The model itself declined, and said why.</summary>
    ModelDeclined,

    /// <summary>The repository has no allow list, so nothing is requestable yet (section 7.3).</summary>
    RepoNotConfigured,
}

/// <summary>
/// What one refinement turn produced (section 10).
/// </summary>
/// <remarks>
/// There are four outcomes and only one of them is a spec. That asymmetry is the design: refinement
/// refuses to produce a spec while the request is still ambiguous, so no agent runs and no tokens
/// burn on a vague request. The hierarchy is closed — the constructor is private and every case is
/// nested — so a fifth outcome cannot be added from outside and slip past a switch.
/// </remarks>
public abstract record RefinementOutcome
{
    private RefinementOutcome()
    {
    }

    /// <summary>Plain language for the requester. Never contains a repo path (section 7.4).</summary>
    public abstract string RequesterMessage { get; }

    /// <summary>Something is still ambiguous. No spec, no dispatch.</summary>
    /// <param name="Questions">What needs settling, in plain language.</param>
    /// <param name="Message">The lead-in shown above the questions.</param>
    public sealed record NeedsClarification(IReadOnlyList<string> Questions, string Message)
        : RefinementOutcome
    {
        /// <inheritdoc />
        public override string RequesterMessage => Message;
    }

    /// <summary>A spec, ready to be put in front of the requester as a confirmation card.</summary>
    /// <param name="Spec">The structured spec — the single source of truth.</param>
    /// <param name="Flags">Instruction-shaped language found in the submitted text (section 16).</param>
    public sealed record SpecProposed(SpecDocument Spec, IReadOnlyList<InjectionSignal> Flags)
        : RefinementOutcome
    {
        /// <summary>Whether an engineer must read the flags before anything is dispatched.</summary>
        public bool RequiresEngineerReview => Flags.Count > 0;

        /// <inheritdoc />
        public override string RequesterMessage =>
            RequiresEngineerReview
                ? "Here's what I've understood. An engineer is taking a quick look before it goes ahead."
                : "Here's what I've understood — have a read and confirm it's right.";

        /// <summary>The confirmation card for this spec, checked against the repo's scope.</summary>
        public SpecConfirmationCard Card(RefinementScopePolicy scope) =>
            SpecConfirmationCard.For(Spec, scope, RequiresEngineerReview);
    }

    /// <summary>This cannot be built here. Routed to an engineer.</summary>
    /// <param name="Reason">Why.</param>
    /// <param name="Message">Plain English, no paths.</param>
    /// <param name="EngineerDetail">The specifics, for whoever picks it up.</param>
    public sealed record Refused(RefusalReason Reason, string Message, string EngineerDetail)
        : RefinementOutcome
    {
        /// <inheritdoc />
        public override string RequesterMessage => Message;
    }

    /// <summary>An answer, in Chat mode. Produces nothing (section 10b).</summary>
    /// <param name="Message">The answer.</param>
    public sealed record Answered(string Message) : RefinementOutcome
    {
        /// <inheritdoc />
        public override string RequesterMessage => Message;
    }
}

/// <summary>
/// A refinement turn: what it produced, and what it cost.
/// </summary>
/// <param name="Outcome">What the turn produced.</param>
/// <param name="Mode">The mode the conversation was in.</param>
/// <param name="Usage">Token counts, for the ledger (section 34).</param>
/// <param name="Charge">What it cost, and against whose grant (section 20b.5).</param>
public sealed record RefinementResult(
    RefinementOutcome Outcome,
    InteractionMode Mode,
    ModelUsage Usage,
    ModelCharge Charge)
{
    /// <summary>The spec, when the turn produced one.</summary>
    public SpecDocument? Spec =>
        Outcome is RefinementOutcome.SpecProposed proposed ? proposed.Spec : null;
}
