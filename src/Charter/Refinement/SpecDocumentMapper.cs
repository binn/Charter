using System.Text.Json;
using Charter.Domain;

namespace Charter.Refinement;

/// <summary>
/// Maps between the structured <see cref="SpecDocument"/> and the persisted <see cref="Spec"/> row
/// of section 5.
/// </summary>
/// <remarks>
/// <para>
/// The structured fields are what is stored and what is read back. <see cref="Spec.BodyMd"/> is
/// <em>derived</em>: <see cref="ToDraft"/> regenerates it from the document on every write and
/// <see cref="ToDocument"/> never reads it. A hand-edited body is therefore overwritten rather than
/// honoured, which is the persistence-layer expression of the section 10b rule that the structured
/// spec is the single source of truth.
/// </para>
/// </remarks>
public static class SpecDocumentMapper
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Writes a structured spec to a new draft row.</summary>
    public static Spec ToDraft(
        SpecDocument document,
        Guid requestId,
        int version = 1,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Spec.Draft(
            requestId,
            version,
            document.Title,
            document.Outcome,
            document.ForEngineer().Render().Markdown,
            JsonSerializer.Serialize(document.AcceptanceCriteria, Options),
            document.TechnicalApproach,
            JsonSerializer.Serialize(
                new { files = document.Scope.Files, paths = document.Scope.Paths },
                Options),
            JsonSerializer.Serialize(document.Risks, Options),
            JsonSerializer.Serialize(document.OpenQuestions, Options),
            now,
            id);
    }

    /// <summary>Reads a stored row back as a structured spec, ignoring the derived body.</summary>
    public static SpecDocument ToDocument(Spec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var scope = SpecScope.Empty;
        if (!string.IsNullOrWhiteSpace(spec.Scope))
        {
            var parsed = JsonSerializer.Deserialize<StoredScope>(spec.Scope, Options);
            scope = SpecScope.Of(parsed?.Files, parsed?.Paths);
        }

        return SpecDocument.Create(
            spec.Title,
            spec.Outcome,
            ReadList(spec.AcceptanceCriteria),
            spec.TechnicalApproach,
            scope,
            ReadList(spec.Risks),
            ReadList(spec.OpenQuestions));
    }

    private static IReadOnlyList<string> ReadList(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];

    private sealed class StoredScope
    {
        public List<string>? Files { get; init; }

        public List<string>? Paths { get; init; }
    }
}
