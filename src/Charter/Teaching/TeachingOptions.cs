using Charter.Models;

namespace Charter.Teaching;

/// <summary>Knobs for teaching (sections 13, 34.6). Registered with defaults; the host may replace it.</summary>
public sealed record TeachingOptions
{
    /// <summary>The model teaching runs on.</summary>
    public ModelIdentifier Model { get; init; } = ModelIdentifier.Parse("claude-sonnet-4-6");

    /// <summary>Upper bound on generated tokens for a walkthrough at the most verbose calibration.</summary>
    public int MaxOutputTokens { get; init; } = 3072;

    /// <summary>Sampling temperature, where the provider accepts one.</summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// How many concepts from the ledger are injected into the prompt. Section 13: <em>cap injection
    /// at a few dozen most-recent concepts or the prompt bloats and cost creeps</em>.
    /// </summary>
    public int ConceptInjectionLimit { get; init; } = 40;

    /// <summary>How many transcript events are rendered into a teaching prompt.</summary>
    public int MaxEventsInPrompt { get; init; } = 80;

    /// <summary>Characters of any single event payload rendered into the prompt.</summary>
    public int MaxEventPayloadCharacters { get; init; } = 300;

    /// <summary>
    /// The per-user daily cap on <em>explain this</em>. Section 13: it is the unbounded surface, so
    /// it is the one that needs a cap.
    /// </summary>
    public int ExplainThisPerUserPerDay { get; init; } = 20;

    /// <summary>Tokens for one <em>explain this</em> answer. Much smaller than a walkthrough.</summary>
    public int ExplainThisMaxOutputTokens { get; init; } = 700;

    /// <summary>Tokens for the single call that annotates the whole milestone list.</summary>
    public int AnnotationMaxOutputTokens { get; init; } = 800;

    /// <summary>Characters allowed in one milestone annotation. Section 13 says one sentence each.</summary>
    public int MaxAnnotationCharacters { get; init; } = 240;

    /// <summary>Who a capped user should ask. Named on every limit message (section 34.5).</summary>
    public string CapEscalationContact { get; init; } = "an administrator on this Charter instance";
}
