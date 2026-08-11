using Charter.Domain;

namespace Charter.Orchestration;

/// <summary>
/// Everything one refinement turn changed, as rows rather than as wire shapes.
/// </summary>
/// <remarks>
/// Rows, deliberately. This is the payload of a seam whose whole purpose is that
/// <c>Charter.Orchestration</c> does not depend on <c>Charter.Api.Contracts</c>, and a record carrying
/// <c>RefinementMessageResponse</c> would have moved the dependency rather than inverted it. What
/// crosses is what the handler already had in hand.
/// </remarks>
public sealed record RefinementTurn
{
    /// <summary>The request whose thread this turn belongs to, at its new status.</summary>
    public required Request Request { get; init; }

    /// <summary>The repository, for the glossary the requester's spec card carries (section 8).</summary>
    public required Repo Repo { get; init; }

    /// <summary>The conversation, including the turns this turn appended.</summary>
    public required ConversationRecord Conversation { get; init; }

    /// <summary>The current specification, once refinement has produced one.</summary>
    public Spec? Spec { get; init; }

    /// <summary>
    /// The turns this turn appended, by id.
    /// </summary>
    /// <remarks>
    /// Ids rather than a count, because the projection drops turn kinds a requester is not meant to
    /// read (a mode promotion, an engineer note) and index arithmetic over the two lists would
    /// silently publish the wrong line the first time one of those appeared mid-conversation.
    /// </remarks>
    public IReadOnlyList<Guid> AppendedTurnIds { get; init; } = [];
}

/// <summary>
/// The seam that lets a refinement turn reach the status thread while it is still happening
/// (section 11).
/// </summary>
/// <remarks>
/// <para>
/// Section 11 is explicit that <em>something</em> must stream: "a 5–20 minute silent gap reads as
/// broken". Refinement is the first thing a requester ever experiences and it is entirely
/// model-paced, so it is the gap that matters most — and until this existed, the handler wrote
/// correct rows and the thread only moved when somebody reloaded.
/// </para>
/// <para>
/// It is declared here, in the layer that needs it, and implemented in the layer that already speaks
/// both vocabularies. Section 2.3 still holds either way: every frame published through this is
/// derivable from the rows the same turn wrote, and a client that reconnects and refetches sees the
/// same thing without any of them.
/// </para>
/// </remarks>
public interface IRefinementStream
{
    /// <summary>Publishes what one refinement turn changed. Never throws for a delivery problem.</summary>
    Task PublishAsync(RefinementTurn turn, CancellationToken cancellationToken = default);
}
