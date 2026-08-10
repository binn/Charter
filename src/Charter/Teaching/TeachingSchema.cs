using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Models;

namespace Charter.Teaching;

/// <summary>
/// The structured-output contract for teaching (section 13).
/// </summary>
/// <remarks>
/// <c>concepts_explained</c> is the interesting field. It is what feeds the per-user concept ledger,
/// and asking the model to declare what it just taught is far more reliable than trying to infer it
/// from the prose afterwards. Everything named there is a thing this reader will never be taught
/// twice.
/// </remarks>
public static class TeachingSchema
{
    /// <summary>The schema name for a narrative pass — walkthrough or explain-this.</summary>
    public const string NarrativeName = "charter_teaching_narrative";

    /// <summary>The schema name for the milestone annotation pass.</summary>
    public const string AnnotationName = "charter_teaching_annotations";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The narrative JSON Schema.</summary>
    public const string NarrativeJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["body_md"],
          "properties": {
            "body_md": {
              "type": "string",
              "description": "Markdown. Grounded in this session's real events, never generic."
            },
            "concepts_explained": {
              "type": "array",
              "description": "Short lower-case names of concepts you defined or taught here.",
              "items": { "type": "string" }
            }
          }
        }
        """;

    /// <summary>The annotation JSON Schema.</summary>
    public const string AnnotationJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["annotations"],
          "properties": {
            "annotations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["index", "sentence"],
                "properties": {
                  "index": { "type": "integer" },
                  "sentence": { "type": "string" }
                }
              }
            },
            "concepts_explained": {
              "type": "array",
              "items": { "type": "string" }
            }
          }
        }
        """;

    /// <summary>The prose form of the narrative contract.</summary>
    public const string NarrativeInstructions = """
        Return a single JSON object and nothing else.

        {
          "body_md": "the explanation, in markdown",
          "concepts_explained": ["short lower-case names of things you defined here"]
        }

        List a concept in "concepts_explained" only if you actually defined or taught it in this
        answer. It is used to make sure this person is never taught the same thing twice.
        """;

    /// <summary>The prose form of the annotation contract.</summary>
    public const string AnnotationInstructions = """
        Return a single JSON object and nothing else.

        {
          "annotations": [ { "index": 0, "sentence": "one sentence about this milestone" } ],
          "concepts_explained": ["short lower-case names of things you defined here"]
        }

        One entry per milestone, using the index you were given. Exactly one sentence each.
        """;

    /// <summary>The narrative response format.</summary>
    public static ModelResponseFormat NarrativeFormat { get; } = new(NarrativeName, NarrativeJsonSchema);

    /// <summary>The annotation response format.</summary>
    public static ModelResponseFormat AnnotationFormat { get; } = new(AnnotationName, AnnotationJsonSchema);

    /// <summary>Parses a narrative response.</summary>
    /// <exception cref="TeachingException">The response was not usable.</exception>
    public static TeachingNarrativePayload ParseNarrative(string json)
        => Parse<TeachingNarrativePayload>(json);

    /// <summary>Parses an annotation response.</summary>
    /// <exception cref="TeachingException">The response was not usable.</exception>
    public static TeachingAnnotationPayload ParseAnnotations(string json)
        => Parse<TeachingAnnotationPayload>(json);

    private static T Parse<T>(string json)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);

        var start = json.IndexOf('{', StringComparison.Ordinal);
        var end = json.LastIndexOf('}');
        var trimmed = start >= 0 && end > start ? json[start..(end + 1)] : json;

        try
        {
            return JsonSerializer.Deserialize<T>(trimmed, Options)
                ?? throw new TeachingException("The teaching model returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new TeachingException(
                "The teaching model returned something that was not the expected JSON.",
                ex);
        }
    }
}

/// <summary>A walkthrough or an explain-this answer.</summary>
public sealed class TeachingNarrativePayload
{
    /// <summary>The explanation.</summary>
    [JsonPropertyName("body_md")]
    public string? BodyMarkdown { get; init; }

    /// <summary>What was defined here, for the concept ledger.</summary>
    [JsonPropertyName("concepts_explained")]
    public IReadOnlyList<string>? ConceptsExplained { get; init; }
}

/// <summary>The single call over the milestone list.</summary>
public sealed class TeachingAnnotationPayload
{
    /// <summary>One entry per milestone.</summary>
    [JsonPropertyName("annotations")]
    public IReadOnlyList<TeachingAnnotationEntry>? Annotations { get; init; }

    /// <summary>What was defined here, for the concept ledger.</summary>
    [JsonPropertyName("concepts_explained")]
    public IReadOnlyList<string>? ConceptsExplained { get; init; }
}

/// <summary>One milestone annotation as the model returned it.</summary>
public sealed class TeachingAnnotationEntry
{
    /// <summary>The index the milestone was presented under.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>One sentence.</summary>
    [JsonPropertyName("sentence")]
    public string? Sentence { get; init; }
}

/// <summary>Teaching could not be produced.</summary>
public sealed class TeachingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TeachingException()
        : base("Teaching failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public TeachingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TeachingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
