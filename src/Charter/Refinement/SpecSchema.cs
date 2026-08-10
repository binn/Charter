using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Models;

namespace Charter.Refinement;

/// <summary>What the refiner decided to do with a turn.</summary>
public enum RefinementResolution
{
    /// <summary>Something is still ambiguous. Questions, not a spec.</summary>
    Clarify,

    /// <summary>A specification.</summary>
    Spec,

    /// <summary>This cannot be done here. Route to an engineer.</summary>
    Refuse,

    /// <summary>An answer, in Chat mode.</summary>
    Answer,
}

/// <summary>
/// The structured-output contract for refinement (section 10b, section 20b.1).
/// </summary>
/// <remarks>
/// The spec is a document with a shape, so it comes back as JSON conforming to a schema rather than
/// as prose to be pattern-matched. <see cref="ModelResponseFormat"/> asks the provider to enforce
/// it; providers that cannot enforce it hint, which is why <see cref="Parse"/> validates rather than
/// trusting.
/// </remarks>
public static class SpecSchema
{
    /// <summary>The schema name providers that require one will see.</summary>
    public const string Name = "charter_refinement";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The JSON Schema document, as JSON text.</summary>
    public const string JsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["resolution"],
          "properties": {
            "resolution": {
              "type": "string",
              "enum": ["clarify", "spec", "refuse", "answer"]
            },
            "message": {
              "type": "string",
              "description": "Plain language for the requester. No file names, no jargon."
            },
            "clarifying_questions": {
              "type": "array",
              "items": { "type": "string" }
            },
            "spec": {
              "type": "object",
              "additionalProperties": false,
              "required": ["title", "outcome", "acceptance_criteria"],
              "properties": {
                "title": { "type": "string" },
                "outcome": { "type": "string" },
                "acceptance_criteria": {
                  "type": "array",
                  "minItems": 1,
                  "items": { "type": "string" }
                },
                "technical_approach": { "type": "string" },
                "scope": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "files": { "type": "array", "items": { "type": "string" } },
                    "paths": { "type": "array", "items": { "type": "string" } }
                  }
                },
                "risks": { "type": "array", "items": { "type": "string" } },
                "open_questions": { "type": "array", "items": { "type": "string" } }
              }
            }
          }
        }
        """;

    /// <summary>The prose form of the contract, for providers that only hint at schemas.</summary>
    public const string Instructions = """
        Return a single JSON object and nothing else.

        {
          "resolution": "clarify" | "spec" | "refuse" | "answer",
          "message": "plain language for the person who asked",
          "clarifying_questions": ["..."],
          "spec": {
            "title": "...",
            "outcome": "what the person will see change, in plain language",
            "acceptance_criteria": ["things they can check afterwards, in their own words"],
            "technical_approach": "for an engineer",
            "scope": { "files": ["..."], "paths": ["..."] },
            "risks": ["..."],
            "open_questions": ["anything you could not settle"]
          }
        }

        Use "clarify" whenever anything material is unsettled, and put the questions in
        "clarifying_questions". Use "refuse" when the change cannot be made here. Only use "spec"
        when "open_questions" would be empty.
        """;

    /// <summary>The response format to attach to a refinement request.</summary>
    public static ModelResponseFormat ResponseFormat { get; } = new(Name, JsonSchema);

    /// <summary>Parses a model response.</summary>
    /// <exception cref="RefinementException">The response was not usable.</exception>
    public static RefinementPayload Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var trimmed = ExtractObject(json);
        RefinementPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RefinementPayload>(trimmed, Options);
        }
        catch (JsonException ex)
        {
            throw new RefinementException(
                "The refiner returned something that was not the expected JSON document.",
                ex);
        }

        return payload
            ?? throw new RefinementException("The refiner returned an empty response.");
    }

    // Providers that only hint at a schema sometimes wrap the object in a fenced block or a
    // sentence. Take the outermost object rather than failing the whole turn.
    private static string ExtractObject(string raw)
    {
        var start = raw.IndexOf('{', StringComparison.Ordinal);
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}

/// <summary>The model's structured answer for one refinement turn.</summary>
public sealed class RefinementPayload
{
    /// <summary>What the refiner decided.</summary>
    [JsonPropertyName("resolution")]
    public string Resolution { get; init; } = "clarify";

    /// <summary>Plain language for the requester.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>What still needs settling.</summary>
    [JsonPropertyName("clarifying_questions")]
    public IReadOnlyList<string>? ClarifyingQuestions { get; init; }

    /// <summary>The proposed spec, when there is one.</summary>
    [JsonPropertyName("spec")]
    public SpecPayload? Spec { get; init; }

    /// <summary>The parsed resolution, defaulting to <see cref="RefinementResolution.Clarify"/>.</summary>
    public RefinementResolution ParsedResolution => Resolution?.Trim().ToLowerInvariant() switch
    {
        "spec" => RefinementResolution.Spec,
        "refuse" => RefinementResolution.Refuse,
        "answer" => RefinementResolution.Answer,
        _ => RefinementResolution.Clarify,
    };
}

/// <summary>The spec half of a refinement response.</summary>
public sealed class SpecPayload
{
    /// <summary>A short plain-language name.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>What the requester will see change.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    /// <summary>The contract.</summary>
    [JsonPropertyName("acceptance_criteria")]
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>Engineer-facing.</summary>
    [JsonPropertyName("technical_approach")]
    public string? TechnicalApproach { get; init; }

    /// <summary>Where the change lands.</summary>
    [JsonPropertyName("scope")]
    public ScopePayload? Scope { get; init; }

    /// <summary>Known risks.</summary>
    [JsonPropertyName("risks")]
    public IReadOnlyList<string>? Risks { get; init; }

    /// <summary>Anything unsettled.</summary>
    [JsonPropertyName("open_questions")]
    public IReadOnlyList<string>? OpenQuestions { get; init; }

    /// <summary>Converts to the structured spec, or explains why it cannot.</summary>
    public bool TryToDocument(out SpecDocument? document, out string? problem)
    {
        document = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(Title))
        {
            problem = "the spec had no title";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Outcome))
        {
            problem = "the spec had no outcome";
            return false;
        }

        if (AcceptanceCriteria is null || AcceptanceCriteria.All(string.IsNullOrWhiteSpace))
        {
            problem = "the spec had no acceptance criteria";
            return false;
        }

        document = SpecDocument.Create(
            Title,
            Outcome,
            AcceptanceCriteria,
            TechnicalApproach,
            SpecScope.Of(Scope?.Files, Scope?.Paths),
            Risks,
            OpenQuestions);

        return true;
    }
}

/// <summary>The scope half of a spec payload.</summary>
public sealed class ScopePayload
{
    /// <summary>Individual files.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Directories.</summary>
    [JsonPropertyName("paths")]
    public IReadOnlyList<string>? Paths { get; init; }
}

/// <summary>Refinement could not complete.</summary>
public sealed class RefinementException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RefinementException()
        : base("Refinement failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public RefinementException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RefinementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
