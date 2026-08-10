using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Refinement;

/// <summary>
/// The domain vocabulary from <c>.charter/glossary.yml</c> (section 8).
/// </summary>
/// <remarks>
/// This file punches above its weight. "BOQ", "derate" and "interconnection" mean nothing to a
/// general model, and a refiner that guesses at them produces a spec about the wrong thing. One
/// file, two consumers: it disambiguates the refiner and grounds the teaching pass.
/// </remarks>
public sealed class GlossaryDocument
{
    private readonly Dictionary<string, string> _terms;

    private GlossaryDocument(Dictionary<string, string> terms) => _terms = terms;

    /// <summary>A repo that has not written one yet.</summary>
    public static GlossaryDocument Empty { get; } = new([]);

    /// <summary>Every term, in file order.</summary>
    public IReadOnlyDictionary<string, string> Terms => _terms;

    /// <summary>Whether the repo defined any vocabulary.</summary>
    public bool IsEmpty => _terms.Count == 0;

    /// <summary>Builds a glossary from already-parsed pairs.</summary>
    public static GlossaryDocument From(IEnumerable<KeyValuePair<string, string>> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, definition) in terms)
        {
            if (!string.IsNullOrWhiteSpace(term) && !string.IsNullOrWhiteSpace(definition))
            {
                map[term.Trim()] = definition.Trim();
            }
        }

        return new GlossaryDocument(map);
    }

    /// <summary>
    /// Parses <c>glossary.yml</c>. Section 8: unknown keys warn, never fail, so an old Charter keeps
    /// working against a file written for a newer one.
    /// </summary>
    public static GlossaryDocument Parse(string yaml, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            warnings?.Add($"glossary.yml is not valid YAML and was ignored: {ex.Message}");
            return Empty;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings?.Add("glossary.yml is not a mapping of term to definition and was ignored.");
            return Empty;
        }

        var terms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (valueNode is YamlScalarNode { Value: { Length: > 0 } definition })
            {
                terms[key.Trim()] = definition.Trim();
            }
            else
            {
                warnings?.Add($"glossary.yml: '{key}' is not a plain definition and was ignored.");
            }
        }

        return new GlossaryDocument(terms);
    }

    /// <summary>Renders the vocabulary for the refinement prompt.</summary>
    public string ToPromptText()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var (term, definition) in _terms)
        {
            builder.Append("- ").Append(term).Append(": ").AppendLine(definition);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// The organisation's <c>standards.yml</c> (section 26.2), as refinement consumes it.
/// </summary>
/// <remarks>
/// Section 26.3 names the refiner as the highest-value consumer of this file and says it costs
/// nothing extra: inject the standards into the refinement context, and refinement stops proposing
/// libraries and services the organisation does not use. The pinned <see cref="Version"/> travels
/// with the spec so a later tightening of standards does not retroactively invalidate it
/// (section 26.7).
/// </remarks>
public sealed class StandardsDocument
{
    private StandardsDocument(
        int version,
        IReadOnlyList<string> lines,
        IReadOnlyList<string> requiredFiles)
    {
        Version = version;
        Lines = lines;
        RequiredFiles = requiredFiles;
    }

    /// <summary>No standards repo configured.</summary>
    public static StandardsDocument None { get; } = new(0, [], []);

    /// <summary>The <c>version</c> the spec is pinned against (section 26.7).</summary>
    public int Version { get; }

    /// <summary>Flattened <c>stacks</c>, <c>services</c> and <c>conventions</c> entries.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Files every project must carry.</summary>
    public IReadOnlyList<string> RequiredFiles { get; }

    /// <summary>Whether anything was configured.</summary>
    public bool IsEmpty => Lines.Count == 0 && RequiredFiles.Count == 0;

    /// <summary>
    /// Parses <c>standards.yml</c>. Unknown keys warn and are ignored (section 8 extensibility
    /// rules apply to every Charter YAML file).
    /// </summary>
    public static StandardsDocument Parse(string yaml, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return None;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            warnings?.Add($"standards.yml is not valid YAML and was ignored: {ex.Message}");
            return None;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings?.Add("standards.yml is not a YAML mapping and was ignored.");
            return None;
        }

        var version = 0;
        var lines = new List<string>();
        var requiredFiles = new List<string>();

        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            switch (key)
            {
                case "version":
                    if (valueNode is YamlScalarNode { Value: { Length: > 0 } raw }
                        && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        version = parsed;
                    }

                    break;

                case "required_files":
                    if (valueNode is YamlSequenceNode files)
                    {
                        requiredFiles.AddRange(files.Children
                            .OfType<YamlScalarNode>()
                            .Select(static node => node.Value)
                            .Where(static value => !string.IsNullOrWhiteSpace(value))
                            .Select(static value => value!.Trim()));
                    }

                    break;

                case "stacks":
                case "services":
                case "conventions":
                case "scaffolding":
                    Flatten(key, valueNode, lines);
                    break;

                case "deviations":
                    // Deviations are per-project and recorded in the project's own config (26.6).
                    break;

                default:
                    warnings?.Add($"standards.yml: unknown key '{key}' was ignored.");
                    break;
            }
        }

        return new StandardsDocument(version, lines, requiredFiles);
    }

    /// <summary>Renders the standards for the refinement prompt.</summary>
    public string ToPromptText()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in Lines)
        {
            builder.Append("- ").AppendLine(line);
        }

        foreach (var file in RequiredFiles)
        {
            builder.Append("- every project carries ").AppendLine(file);
        }

        return builder.ToString().TrimEnd();
    }

    private static void Flatten(string prefix, YamlNode node, List<string> into)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { Length: > 0 } scalar }:
                into.Add($"{prefix}: {scalar}");
                break;

            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is YamlScalarNode { Value: { Length: > 0 } key })
                    {
                        Flatten($"{prefix}.{key}", valueNode, into);
                    }
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children.OfType<YamlScalarNode>())
                {
                    if (!string.IsNullOrWhiteSpace(child.Value))
                    {
                        into.Add($"{prefix}: {child.Value!.Trim()}");
                    }
                }

                break;
        }
    }
}

/// <summary>
/// Everything refinement is grounded in for one repository (sections 8, 10 and 26.3).
/// </summary>
/// <remarks>
/// All of it is repository- or organisation-authored and committed, which is the point: changing
/// what refinement is allowed to propose means opening a pull request against the repo or the
/// standards repo, not editing a settings page.
/// </remarks>
public sealed record RefinementContext
{
    /// <summary>A repo with nothing configured. Deny by default; refinement refuses everything.</summary>
    public static RefinementContext Bare { get; } = new();

    /// <summary>Domain vocabulary from <c>.charter/glossary.yml</c>.</summary>
    public GlossaryDocument Glossary { get; init; } = GlossaryDocument.Empty;

    /// <summary>Codebase shape from <c>.charter/primer.md</c>.</summary>
    public string PrimerMarkdown { get; init; } = string.Empty;

    /// <summary>Agent guidance from <c>.charter/conventions.md</c>.</summary>
    public string ConventionsMarkdown { get; init; } = string.Empty;

    /// <summary>The organisation's <c>standards.yml</c> (section 26.3).</summary>
    public StandardsDocument Standards { get; init; } = StandardsDocument.None;

    /// <summary>The repo's <c>scopes</c> lists (section 8).</summary>
    public RefinementScopePolicy Scope { get; init; } = RefinementScopePolicy.DenyEverything;

    /// <summary>A display name for the project, used in requester-facing copy.</summary>
    public string ProjectName { get; init; } = "this project";
}
