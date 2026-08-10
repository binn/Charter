namespace Charter.Domain;

/// <summary>
/// Every concept already explained to one person (section 13). Passed into the teaching prompt as
/// <em>already knows: X, Y, Z</em> so a concept is referenced next time rather than re-taught.
/// </summary>
/// <remarks>
/// This is what lets an <c>explain_everything</c> requester graduate organically over fifteen
/// sessions without touching a setting. Injection is capped at a few dozen most-recent concepts or
/// the prompt bloats and cost creeps, and the whole ledger is resettable, because people forget.
/// </remarks>
public sealed class ConceptLedger
{
    private ConceptLedger()
    {
    }

    private ConceptLedger(Guid id, Guid userId, string concept, DateTimeOffset firstExplainedAt)
    {
        Id = id;
        UserId = userId;
        Concept = concept;
        FirstExplainedAt = firstExplainedAt;
        LastReferencedAt = firstExplainedAt;
        TimesReferenced = 1;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Normalised to lower case so "Migration" and "migration" are one concept.</summary>
    public string Concept { get; private set; } = string.Empty;

    public DateTimeOffset FirstExplainedAt { get; private set; }

    /// <summary>Orders the capped injection window: most recent concepts first.</summary>
    public DateTimeOffset LastReferencedAt { get; private set; }

    public int TimesReferenced { get; private set; }

    public static ConceptLedger Record(Guid userId, string concept, DateTimeOffset? now = null, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concept);

        return new ConceptLedger(
            id ?? Guid.CreateVersion7(),
            userId,
            concept.Trim().ToLowerInvariant(),
            DomainTime.Resolve(now));
    }

    public void Reference(DateTimeOffset? now = null)
    {
        TimesReferenced++;
        LastReferencedAt = DomainTime.Resolve(now);
    }
}
