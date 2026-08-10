namespace Charter.Models;

/// <summary>
/// One event in a streamed response. Refinement is interactive, so the conversation streams; the
/// terminal <see cref="Completed"/> event carries the assembled text, usage and cost so a streaming
/// caller gets the same accounting a single-shot caller does.
/// </summary>
public abstract record ModelStreamEvent
{
    private ModelStreamEvent()
    {
    }

    /// <summary>Incremental response text.</summary>
    /// <param name="Text">The delta. Concatenating every delta in order yields the full response.</param>
    public sealed record TextDelta(string Text) : ModelStreamEvent;

    /// <summary>
    /// Incremental reasoning text, where the provider surfaces it. Never counted as response text.
    /// </summary>
    /// <param name="Text">The delta.</param>
    public sealed record ReasoningDelta(string Text) : ModelStreamEvent;

    /// <summary>The stream finished. Always the last event of a successful stream.</summary>
    /// <param name="Completion">The assembled response, including usage and cost.</param>
    public sealed record Completed(ModelCompletion Completion) : ModelStreamEvent;
}
