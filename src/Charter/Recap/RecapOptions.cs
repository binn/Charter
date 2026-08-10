using Charter.Models;

// The folder is Recap/ and the namespace is Charter.Recaps, deliberately. `Charter.Domain.Recap` is
// an entity, and a sibling namespace called `Charter.Recap` would shadow it inside every file that
// says `using Charter.Domain;` — the type would silently become unreachable by its own name. The
// plural costs one letter and keeps the entity addressable everywhere.
namespace Charter.Recaps;

/// <summary>Knobs for the engineer recap (section 14). Registered with defaults; the host may replace it.</summary>
public sealed record RecapOptions
{
    /// <summary>The model the recap runs on.</summary>
    public ModelIdentifier Model { get; init; } = ModelIdentifier.Parse("claude-sonnet-4-6");

    /// <summary>Upper bound on generated tokens.</summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>
    /// Sampling temperature. A recap wants the same session to read the same way twice, so this is
    /// left unset — and where a provider defaults it high, a host can pin it here.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// How many transcript events are rendered into the prompt, newest-relevant first. The event
    /// table is the largest in the database by orders of magnitude (section 5); a recap that fed all
    /// of it into a prompt would cost more than the build.
    /// </summary>
    public int MaxEventsInPrompt { get; init; } = 120;

    /// <summary>How many files the suggested review order names before it stops.</summary>
    public int ReviewOrderLength { get; init; } = 10;

    /// <summary>Characters of any single event payload rendered into the prompt.</summary>
    public int MaxEventPayloadCharacters { get; init; } = 400;
}
