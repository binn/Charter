namespace Charter.Models;

/// <summary>Who authored a turn in a control-plane conversation.</summary>
public enum ModelRole
{
    /// <summary>The human side of the conversation. Refinement turns are authored here.</summary>
    User,

    /// <summary>The model's own prior turns, replayed to keep a refinement conversation coherent.</summary>
    Assistant,
}

/// <summary>A single turn in a control-plane conversation.</summary>
/// <param name="Role">Who authored the turn.</param>
/// <param name="Content">The turn text.</param>
public sealed record ModelMessage(ModelRole Role, string Content)
{
    /// <summary>A user turn.</summary>
    public static ModelMessage User(string content) => new(ModelRole.User, content);

    /// <summary>An assistant turn, replayed from an earlier response.</summary>
    public static ModelMessage Assistant(string content) => new(ModelRole.Assistant, content);
}

/// <summary>
/// A JSON schema the response must conform to. Refinement needs structured output - the refined spec
/// is a document with a shape, not free text.
/// </summary>
/// <param name="Name">A short schema name. Some providers require one.</param>
/// <param name="JsonSchema">The JSON Schema document, as JSON text.</param>
/// <param name="Strict">
/// Whether to ask the provider to enforce the schema rather than merely hint at it. Providers that
/// cannot enforce it fall back to hinting.
/// </param>
public sealed record ModelResponseFormat(string Name, string JsonSchema, bool Strict = true);

/// <summary>
/// One control-plane model invocation. Multi-turn with a system prompt, because refinement is a
/// conversation; teaching and recap simply pass a single user turn.
/// </summary>
public sealed record ModelRequest
{
    /// <summary>The provider-qualified model to invoke.</summary>
    public required ModelIdentifier Model { get; init; }

    /// <summary>
    /// The system prompt. Section 16: this is Charter's own instruction text, never raw requester
    /// input - the agent and the refiner see model-authored, human-approved text.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The conversation so far, oldest first. Must contain at least one turn.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    /// <summary>Upper bound on generated tokens.</summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>Sampling temperature, where the provider accepts one.</summary>
    public double? Temperature { get; init; }

    /// <summary>The schema the response must conform to, when structured output is wanted.</summary>
    public ModelResponseFormat? ResponseFormat { get; init; }

    /// <summary>Sequences that end generation early.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>
    /// A caller-supplied correlation label - session id, request id - carried into provider metadata
    /// where the provider supports it. Never a credential and never requester text.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Builds a single-shot request, the shape teaching and recap use.</summary>
    public static ModelRequest SingleShot(
        ModelIdentifier model,
        string? systemPrompt,
        string userPrompt,
        int maxOutputTokens = 4096) => new()
        {
            Model = model,
            SystemPrompt = systemPrompt,
            Messages = [ModelMessage.User(userPrompt)],
            MaxOutputTokens = maxOutputTokens,
        };
}

/// <summary>Why generation stopped.</summary>
public enum ModelStopReason
{
    /// <summary>The provider did not say.</summary>
    Unknown,

    /// <summary>The model finished its turn.</summary>
    EndTurn,

    /// <summary>Generation hit <see cref="ModelRequest.MaxOutputTokens"/>.</summary>
    MaxTokens,

    /// <summary>A stop sequence matched.</summary>
    StopSequence,

    /// <summary>The model declined.</summary>
    Refusal,
}
