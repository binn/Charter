using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Recaps;

/// <summary>One departure from the approved specification, as it is stored.</summary>
/// <remarks>
/// The fields are kept apart rather than pre-joined into a sentence because the distinction between
/// <em>"the spec said X and it did Y"</em> and <em>"the spec was silent and it chose Y"</em> is the
/// one a reviewer scrutinises differently, and a joined sentence loses it.
/// </remarks>
public sealed record RecapDocumentDeviation
{
    /// <summary>What the agent did. Always present; a deviation without one is dropped.</summary>
    [JsonPropertyName("what")]
    public required string What { get; init; }

    /// <summary>What the specification asked for, where it asked for anything.</summary>
    [JsonPropertyName("spec_said")]
    public string? SpecSaid { get; init; }

    /// <summary>The reason, where the transcript gave one.</summary>
    [JsonPropertyName("why")]
    public string? Why { get; init; }

    /// <summary>The file or step it happened in.</summary>
    [JsonPropertyName("where")]
    public string? Where { get; init; }
}

/// <summary>
/// The structured engineer recap, stored as <c>recaps.payload</c> jsonb beside the prose.
/// </summary>
/// <remarks>
/// <para>
/// <c>body_md</c> is what gets posted as a change request comment (section 14), so it stays. This is
/// the same content before it was rendered: the prose sections as data, scrubbed by
/// <see cref="RecapVerdictGuard"/> exactly as the markdown was, so nothing here can say
/// <em>looks good</em> when the body does not.
/// </para>
/// <para>
/// It exists so the API does not have to parse section headings back out of markdown to serve the
/// recap card. That parse coupled the wire format to a heading string: renaming
/// <c>### 2. Where this deviated from the specification</c> silently emptied a section of the API
/// response, and nothing about the rename looked like an API change.
/// </para>
/// <para>
/// The file list is <em>not</em> duplicated here — it lives in <c>risk_items</c>, in the order
/// <see cref="RecapFileRiskRanker"/> produced, and now carries line counts. Two copies of a ranking
/// is two rankings that can disagree.
/// </para>
/// </remarks>
public sealed record RecapDocument
{
    /// <summary>The current payload version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The payload version, so a reader can tell an old row from a malformed one.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Section 7.5: nobody vetted the specification before the build.</summary>
    [JsonPropertyName("auto_dispatched")]
    public bool AutoDispatched { get; init; }

    /// <summary>Section 14 part 1 — one paragraph, tied back to the approved spec.</summary>
    [JsonPropertyName("summary_md")]
    public string SummaryMd { get; init; } = string.Empty;

    /// <summary>
    /// The specification in full, and only when <see cref="AutoDispatched"/> is true (section 7.5).
    /// </summary>
    [JsonPropertyName("spec_md")]
    public string? SpecMd { get; init; }

    /// <summary>Section 14 part 2, the highest-value section.</summary>
    [JsonPropertyName("deviations")]
    public IReadOnlyList<RecapDocumentDeviation> Deviations { get; init; } = [];

    /// <summary>Section 14 part 4 — tests not written, edge cases noticed and skipped.</summary>
    [JsonPropertyName("could_not_verify")]
    public IReadOnlyList<string> CouldNotVerify { get; init; } = [];

    /// <summary>
    /// How many quality judgements the verdict guard removed on the way in. Recorded rather than
    /// hidden: a model that trips this constantly is worth replacing (section 14).
    /// </summary>
    [JsonPropertyName("verdicts_removed")]
    public int VerdictsRemoved { get; init; }

    /// <summary>An empty document, for a row written before the column existed.</summary>
    public static RecapDocument Empty { get; } = new();

    /// <summary>Serialises the payload for the <c>jsonb</c> column.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads a stored payload, returning <see cref="Empty"/> for anything unreadable.
    /// </summary>
    /// <remarks>
    /// A row written by a build that spelled the payload differently is not a reason to fail a whole
    /// request detail, so this never throws. The caller can tell the two apart:
    /// <see cref="Empty"/> has an empty <see cref="SummaryMd"/>, and a real recap never does.
    /// </remarks>
    public static RecapDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<RecapDocument>(json, Json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
