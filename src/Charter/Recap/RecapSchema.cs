using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Models;

namespace Charter.Recaps;

/// <summary>
/// The structured-output contract for the engineer recap (section 14).
/// </summary>
/// <remarks>
/// The model fills in the prose. It does <em>not</em> order the file list and it does not choose the
/// review order — <see cref="RecapFileRiskRanker"/> does both, so the ordering an engineer acts on
/// cannot come back alphabetical on a bad day. Structured output also keeps the five sections in
/// section 14's order regardless of what order the model wrote them in.
/// </remarks>
public static class RecapSchema
{
    /// <summary>The schema name providers that require one will see.</summary>
    public const string Name = "charter_engineer_recap";

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
          "required": ["what_and_why", "deviations", "could_not_verify"],
          "properties": {
            "what_and_why": {
              "type": "string",
              "description": "One paragraph. What the session did and why, tied to the approved spec."
            },
            "deviations": {
              "type": "array",
              "description": "Where the agent departed from the spec or made a call the spec did not cover.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["what"],
                "properties": {
                  "what": { "type": "string" },
                  "spec_said": { "type": "string" },
                  "why": { "type": "string" },
                  "where": { "type": "string" }
                }
              }
            },
            "file_notes": {
              "type": "array",
              "description": "One short factual line per file. No judgement about quality.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["path", "note"],
                "properties": {
                  "path": { "type": "string" },
                  "note": { "type": "string" }
                }
              }
            },
            "could_not_verify": {
              "type": "array",
              "description": "Tests not written, checks not run, edge cases noticed and skipped.",
              "items": { "type": "string" }
            }
          }
        }
        """;

    /// <summary>The prose form of the contract, for providers that only hint at schemas.</summary>
    public const string Instructions = """
        Return a single JSON object and nothing else.

        {
          "what_and_why": "one paragraph, tied back to the approved specification",
          "deviations": [
            {
              "what": "what the agent did differently",
              "spec_said": "what the specification asked for, quoted or paraphrased",
              "why": "the reason, if the transcript gives one",
              "where": "file or step"
            }
          ],
          "file_notes": [ { "path": "exactly as given to you", "note": "what changed in it" } ],
          "could_not_verify": ["tests not written, checks not run, edge cases skipped"]
        }

        "deviations" is the section reviewers most often miss and the most valuable thing you can
        produce. Include a decision the specification simply did not cover — that is a deviation too.
        Return an empty array only when the transcript genuinely shows none, and never pad it.
        """;

    /// <summary>The response format to attach to a recap request.</summary>
    public static ModelResponseFormat ResponseFormat { get; } = new(Name, JsonSchema);

    /// <summary>Parses a model response, tolerating a fenced or prefixed object.</summary>
    /// <exception cref="RecapException">The response was not usable.</exception>
    public static RecapPayload Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var start = json.IndexOf('{', StringComparison.Ordinal);
        var end = json.LastIndexOf('}');
        var trimmed = start >= 0 && end > start ? json[start..(end + 1)] : json;

        RecapPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecapPayload>(trimmed, Options);
        }
        catch (JsonException ex)
        {
            throw new RecapException("The recap model returned something that was not the expected JSON.", ex);
        }

        return payload ?? throw new RecapException("The recap model returned an empty response.");
    }
}

/// <summary>The model's structured answer for one recap.</summary>
public sealed class RecapPayload
{
    /// <summary>Section 14 part 1.</summary>
    [JsonPropertyName("what_and_why")]
    public string? WhatAndWhy { get; init; }

    /// <summary>Section 14 part 2, the highest-value section.</summary>
    [JsonPropertyName("deviations")]
    public IReadOnlyList<RecapDeviationPayload>? Deviations { get; init; }

    /// <summary>One line per file, merged onto the Charter-computed ranking.</summary>
    [JsonPropertyName("file_notes")]
    public IReadOnlyList<RecapFileNotePayload>? FileNotes { get; init; }

    /// <summary>Section 14 part 4.</summary>
    [JsonPropertyName("could_not_verify")]
    public IReadOnlyList<string>? CouldNotVerify { get; init; }
}

/// <summary>One departure from the approved spec.</summary>
public sealed class RecapDeviationPayload
{
    /// <summary>What the agent did.</summary>
    [JsonPropertyName("what")]
    public string? What { get; init; }

    /// <summary>What the spec asked for.</summary>
    [JsonPropertyName("spec_said")]
    public string? SpecSaid { get; init; }

    /// <summary>Why, where the transcript says.</summary>
    [JsonPropertyName("why")]
    public string? Why { get; init; }

    /// <summary>Which file or step.</summary>
    [JsonPropertyName("where")]
    public string? Where { get; init; }
}

/// <summary>One factual line about one file.</summary>
public sealed class RecapFileNotePayload
{
    /// <summary>The path, as it was given to the model.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>What changed in it.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>The recap could not be produced.</summary>
public sealed class RecapException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RecapException()
        : base("The engineer recap failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public RecapException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RecapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
