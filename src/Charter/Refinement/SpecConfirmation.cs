using Charter.Domain;

namespace Charter.Refinement;

/// <summary>Why a confirmation card refused to be confirmed.</summary>
public enum ConfirmationBlocker
{
    /// <summary>The spec still carries open questions, so it is not a spec yet (section 10).</summary>
    OpenQuestions,

    /// <summary>Wording that reads as unresolved — <em>TBD</em>, <em>etc.</em>, <em>as appropriate</em>.</summary>
    VagueAcceptanceCriteria,

    /// <summary>The spec's scope touches paths the repo denies (sections 7.3, 8, 10).</summary>
    DeniedPaths,

    /// <summary>Instruction-shaped language was flagged and no engineer has cleared it (section 16).</summary>
    AwaitingEngineerReview,
}

/// <summary>One reason a spec cannot be confirmed, with language a requester can act on.</summary>
/// <param name="Blocker">What is in the way.</param>
/// <param name="RequesterMessage">Plain English. Never contains a repo path (section 7.4).</param>
/// <param name="EngineerDetail">The specifics, for whoever picks it up.</param>
public sealed record ConfirmationObstacle(
    ConfirmationBlocker Blocker,
    string RequesterMessage,
    string EngineerDetail);

/// <summary>
/// The spec confirmation card of section 10 — the ownership moment.
/// </summary>
/// <remarks>
/// <para>
/// The card renders the requester projection and nothing else, because the requester approves the
/// <em>acceptance criteria</em>, not the technical approach. That is the part they can meaningfully
/// judge, and it is the part quoted back later when a preview is wrong: the conversation becomes
/// "the spec said X" rather than "the AI misunderstood".
/// </para>
/// <para>
/// Confirming is also the only way to obtain an <see cref="ApprovedSpec"/>, and an
/// <see cref="ApprovedSpec"/> is the only way to obtain an <see cref="AgentBriefing"/>. That chain is
/// the dispatch-side half of the section 16 boundary.
/// </para>
/// </remarks>
public sealed class SpecConfirmationCard
{
    private readonly RefinementConversation? _conversation;

    internal SpecConfirmationCard(RefinementConversation conversation, SpecDocument spec)
    {
        _conversation = conversation;
        Spec = spec;
        Obstacles = Evaluate(spec, conversation.RequiresEngineerReview, scope: null);
    }

    private SpecConfirmationCard(SpecDocument spec, IReadOnlyList<ConfirmationObstacle> obstacles)
    {
        Spec = spec;
        Obstacles = obstacles;
    }

    /// <summary>The structured spec being confirmed.</summary>
    public SpecDocument Spec { get; }

    /// <summary>Anything standing in the way of confirmation.</summary>
    public IReadOnlyList<ConfirmationObstacle> Obstacles { get; }

    /// <summary>Whether the requester may confirm.</summary>
    public bool CanConfirm => Obstacles.Count == 0;

    /// <summary>What the requester sees. Title, outcome, acceptance criteria — nothing else.</summary>
    public RequesterSpecView View => Spec.ForRequester();

    /// <summary>The card body, generated from the structured spec on every call.</summary>
    public RenderedSpec Render() => View.Render();

    /// <summary>Builds a card outside a conversation, for a scope check against a repo policy.</summary>
    public static SpecConfirmationCard For(
        SpecDocument spec,
        RefinementScopePolicy scope,
        bool requiresEngineerReview = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(scope);
        return new SpecConfirmationCard(spec, Evaluate(spec, requiresEngineerReview, scope));
    }

    /// <summary>
    /// The ownership moment. Produces the only object a build may be started from.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something still blocks confirmation.</exception>
    public ApprovedSpec Confirm(Guid confirmedBy, DateTimeOffset? now = null)
    {
        if (!CanConfirm)
        {
            throw new InvalidOperationException(
                "This spec is not ready to confirm: "
                + string.Join(" ", Obstacles.Select(static obstacle => obstacle.RequesterMessage)));
        }

        var at = DomainTime.Resolve(now);
        var approved = new ApprovedSpec(Spec, confirmedBy, at);
        _conversation?.Accept(approved, at);
        return approved;
    }

    private static IReadOnlyList<ConfirmationObstacle> Evaluate(
        SpecDocument spec,
        bool requiresEngineerReview,
        RefinementScopePolicy? scope)
    {
        var obstacles = new List<ConfirmationObstacle>();

        if (spec.HasOpenQuestions)
        {
            obstacles.Add(new ConfirmationObstacle(
                ConfirmationBlocker.OpenQuestions,
                "There are still a couple of things I need to know before this is ready. "
                + "Let's settle those first.",
                "Open questions: " + string.Join("; ", spec.OpenQuestions)));
        }

        var vague = AmbiguityGuard.VagueCriteria(spec);
        if (vague.Count > 0)
        {
            obstacles.Add(new ConfirmationObstacle(
                ConfirmationBlocker.VagueAcceptanceCriteria,
                "Some of what we've written down is still too vague to check afterwards. "
                + "Let's make it specific.",
                "Vague acceptance criteria: " + string.Join("; ", vague)));
        }

        if (scope is not null)
        {
            var decision = scope.Evaluate(spec.Scope);
            if (!decision.IsAllowed)
            {
                obstacles.Add(new ConfirmationObstacle(
                    ConfirmationBlocker.DeniedPaths,
                    decision.RequesterMessage,
                    decision.EngineerDetail));
            }
        }

        if (requiresEngineerReview)
        {
            obstacles.Add(new ConfirmationObstacle(
                ConfirmationBlocker.AwaitingEngineerReview,
                "An engineer is taking a quick look at this one before it goes ahead.",
                "Instruction-shaped language was flagged in the submitted text (section 16)."));
        }

        return obstacles;
    }
}

/// <summary>
/// A spec a named human has confirmed. <strong>The only thing a build may be started from.</strong>
/// </summary>
/// <remarks>
/// <para>
/// There is no public constructor and no factory taking text: an <see cref="ApprovedSpec"/> can only
/// come out of <see cref="SpecConfirmationCard.Confirm"/>, which can only be reached from a
/// <see cref="SpecDocument"/>, which is model-authored. Raw requester text has no route here that a
/// compiler would accept, which is what section 16 means when it calls refinement a sanitisation
/// boundary.
/// </para>
/// </remarks>
public sealed class ApprovedSpec
{
    internal ApprovedSpec(SpecDocument spec, Guid confirmedBy, DateTimeOffset confirmedAt)
    {
        Spec = spec;
        ConfirmedBy = confirmedBy;
        ConfirmedAt = confirmedAt;
    }

    /// <summary>The structured spec, unchanged since confirmation.</summary>
    public SpecDocument Spec { get; }

    /// <summary>The named human who confirmed it. Every agent action traces back here (section 7.3).</summary>
    public Guid ConfirmedBy { get; }

    /// <summary>When.</summary>
    public DateTimeOffset ConfirmedAt { get; }

    /// <summary>The fingerprint of what was confirmed, so a later edit is detectable.</summary>
    public string ConfirmedContentHash => Spec.ContentHash;

    /// <summary>What the requester saw when they confirmed.</summary>
    public RenderedSpec ConfirmedView => Spec.ForRequester().Render();
}

/// <summary>
/// Charter's own check that a spec is actually settled, independent of what the model claimed.
/// </summary>
/// <remarks>
/// Section 10 requires refusing to dispatch anything still ambiguous, and a model asked "is this
/// unambiguous?" will sometimes say yes. So the refuser is not the model: a spec carrying open
/// questions, or acceptance criteria hedged with <em>TBD</em> or <em>as appropriate</em>, is sent
/// back for another round regardless of what the model resolved to.
/// </remarks>
public static class AmbiguityGuard
{
    private static readonly string[] HedgeWords =
    [
        "tbd",
        "to be decided",
        "to be determined",
        "etc.",
        "and so on",
        "as appropriate",
        "as needed",
        "if possible",
        "somehow",
        "something like",
        "or similar",
        "maybe",
        "probably",
        "we think",
        "not sure",
        "figure out",
        "???",
    ];

    /// <summary>Acceptance criteria that could not be checked as written.</summary>
    public static IReadOnlyList<string> VagueCriteria(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return [.. spec.AcceptanceCriteria.Where(static criterion =>
        {
            var lowered = criterion.ToLowerInvariant();
            return HedgeWords.Any(hedge => lowered.Contains(hedge, StringComparison.Ordinal));
        })];
    }

    /// <summary>
    /// Everything still unsettled about a spec, as questions to put back to the requester. Empty
    /// means the spec is ready to be confirmed.
    /// </summary>
    public static IReadOnlyList<string> UnresolvedQuestions(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var questions = new List<string>(spec.OpenQuestions);
        questions.AddRange(VagueCriteria(spec).Select(static criterion =>
            $"You'd be checking \"{criterion}\" afterwards — what exactly should that look like?"));

        return questions;
    }

    /// <summary>Whether the spec is settled enough to put in front of a requester.</summary>
    public static bool IsSettled(SpecDocument spec) => UnresolvedQuestions(spec).Count == 0;
}
