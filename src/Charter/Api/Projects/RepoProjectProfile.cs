using System.Globalization;
using System.Text;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Projects;

/// <summary>
/// The requester-facing presentation of a repository, read out of its stored
/// <c>.charter/config.yml</c> snapshot (section 8).
/// </summary>
/// <remarks>
/// <para>
/// This reads only presentation — display name, description, templates, glossary and the spend
/// ceiling shown on the approval queue. It deliberately does <em>not</em> read guardrails;
/// <see cref="Charter.Auth.Authorization.RepoCharterConfig"/> owns those, and duplicating a rule
/// here would let the API and the authoriser disagree.
/// </para>
/// <para>
/// Section 7.1 is why the display name matters: a requester never sees <c>owner/repo</c>. When the
/// committed file names no display name, the fallback humanises the repository's own name segment
/// rather than printing the slug — which is the least-bad option and is visibly a fallback.
/// </para>
/// </remarks>
public sealed record RepoProjectProfile
{
    /// <summary>The operator's display name for the project. Never <c>owner/repo</c>.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<RequestTemplateResponse> Templates { get; init; } = [];

    public IReadOnlyList<GlossaryTermResponse> Glossary { get; init; } = [];

    /// <summary>Section 7.5 <c>max_session_usd</c>: the ceiling a session runs under, if any.</summary>
    public decimal? MaxSessionUsd { get; init; }

    /// <summary>Reads the profile a repository row implies.</summary>
    public static RepoProjectProfile For(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var fallbackName = Humanize(repo.FullName);

        if (string.IsNullOrWhiteSpace(repo.CharterConfigSnapshot))
        {
            return new RepoProjectProfile { Name = fallbackName };
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(repo.CharterConfigSnapshot);
        }
        catch (JsonException)
        {
            // Section 8: an unreadable snapshot must not take the project list down. The guardrail
            // reader fails closed on the same input; presentation just falls back.
            return new RepoProjectProfile { Name = fallbackName };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new RepoProjectProfile { Name = fallbackName };
            }

            var root = document.RootElement;
            var project = TryGetObject(root, "project");

            return new RepoProjectProfile
            {
                Name = ReadString(project, "name")
                       ?? ReadString(project, "display_name")
                       ?? ReadString(root, "name")
                       ?? ReadString(root, "display_name")
                       ?? fallbackName,
                Description = ReadString(project, "description") ?? ReadString(root, "description"),
                Templates = ReadTemplates(root),
                Glossary = ReadGlossary(root),
                MaxSessionUsd = ReadDecimal(TryGetObject(root, "limits"), "max_session_usd")
                                ?? ReadDecimal(TryGetObject(root, "auto_dispatch"), "max_session_usd"),
            };
        }
    }

    /// <summary>
    /// The glossary terms this spec actually mentions, for the inline Explain lens (section 10b).
    /// </summary>
    /// <remarks>
    /// Sending the whole glossary on every request would be both wasteful and noisy, and the lens
    /// only ever expands a term that appears in the text in front of the reader.
    /// </remarks>
    public IReadOnlyList<GlossaryTermResponse> GlossaryFor(params ReadOnlySpan<string> texts)
    {
        if (Glossary.Count == 0)
        {
            return [];
        }

        var haystack = new StringBuilder();
        foreach (var text in texts)
        {
            haystack.Append(text).Append(' ');
        }

        var joined = haystack.ToString();

        return [.. Glossary.Where(term => joined.Contains(term.Term, StringComparison.OrdinalIgnoreCase))];
    }

    private static IReadOnlyList<RequestTemplateResponse> ReadTemplates(JsonElement root)
    {
        if (!root.TryGetProperty("templates", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var templates = new List<RequestTemplateResponse>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(element, "id");
            var name = ReadString(element, "name");

            if (id is null || name is null)
            {
                continue;
            }

            templates.Add(new RequestTemplateResponse
            {
                Id = id,
                Name = name,
                Description = ReadString(element, "description") ?? string.Empty,
                Icon = ReadString(element, "icon"),
                Prompt = ReadString(element, "prompt") ?? string.Empty,
                Fields = ReadTemplateFields(element),
            });
        }

        return templates;
    }

    private static IReadOnlyList<RequestTemplateFieldResponse>? ReadTemplateFields(JsonElement template)
    {
        if (!template.TryGetProperty("fields", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fields = new List<RequestTemplateFieldResponse>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = ReadString(element, "key");
            var label = ReadString(element, "label");

            if (key is null || label is null)
            {
                continue;
            }

            fields.Add(new RequestTemplateFieldResponse
            {
                Key = key,
                Label = label,
                Placeholder = ReadString(element, "placeholder"),
                Required = ReadBool(element, "required") ?? false,
                Multiline = ReadBool(element, "multiline") ?? false,
            });
        }

        return fields.Count == 0 ? null : fields;
    }

    private static IReadOnlyList<GlossaryTermResponse> ReadGlossary(JsonElement root)
    {
        if (!root.TryGetProperty("glossary", out var value))
        {
            return [];
        }

        var terms = new List<GlossaryTermResponse>();

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        terms.Add(new GlossaryTermResponse
                        {
                            Term = property.Name,
                            Definition = property.Value.GetString() ?? string.Empty,
                        });
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var element in value.EnumerateArray())
                {
                    var term = ReadString(element, "term");
                    var definition = ReadString(element, "definition");

                    if (term is not null && definition is not null)
                    {
                        terms.Add(new GlossaryTermResponse { Term = term, Definition = definition });
                    }
                }

                break;

            default:
                break;
        }

        return terms;
    }

    /// <summary>Turns <c>northbeam/quote-tool</c> into <c>Quote tool</c>.</summary>
    private static string Humanize(string fullName)
    {
        var slash = fullName.LastIndexOf('/');
        var segment = slash >= 0 && slash < fullName.Length - 1 ? fullName[(slash + 1)..] : fullName;
        var spaced = segment.Replace('-', ' ').Replace('_', ' ').Trim();

        if (spaced.Length == 0)
        {
            return fullName;
        }

        return string.Concat(
            spaced[..1].ToUpper(CultureInfo.InvariantCulture),
            spaced[1..].ToLower(CultureInfo.InvariantCulture));
    }

    private static JsonElement? TryGetObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? ReadString(JsonElement? block, string name)
        => block is { } element
           && element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool? ReadBool(JsonElement block, string name)
        => block.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static decimal? ReadDecimal(JsonElement? block, string name)
        => block is { } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;
}
