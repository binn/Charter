using System.Globalization;
using System.Text;
using System.Text.Json;
using Charter.Refinement;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Onboarding;

/// <summary>
/// The whole committed <c>.charter/</c> folder of one repository at one commit (section 8).
/// </summary>
/// <remarks>
/// <para>
/// This is the missing piece between a connected repository and a grounded refinement: everything in
/// section 8's folder, parsed, with every complaint collected into <see cref="Warnings"/> rather than
/// thrown. Section 8's extensibility rules say unknown keys warn and never fail, and a folder that is
/// missing entirely is an ordinary state, not an error — section 9 onboards repositories that have
/// never heard of Charter.
/// </para>
/// <para>
/// <c>cache/</c> is excluded deliberately. It is gitignored in the target repository, so there is
/// nothing to read there, and generated recon output is cached on Charter's side instead
/// (<see cref="CharterFolderCache"/>).
/// </para>
/// </remarks>
public sealed record CharterFolder
{
    /// <summary>The folder's path in the target repository.</summary>
    public const string Root = ".charter/";

    /// <summary>The generated-output directory, which is gitignored and never read.</summary>
    public const string CacheDirectory = ".charter/cache/";

    /// <summary>Whether the repository has a <c>.charter/</c> folder at all.</summary>
    public required bool Exists { get; init; }

    /// <summary>The commit this was read at. The cache key, with the repository.</summary>
    public required string CommitSha { get; init; }

    /// <summary><c>config.yml</c>.</summary>
    public CharterConfigDocument Config { get; init; } = CharterConfigDocument.Empty;

    /// <summary><c>conventions.md</c> — agent guidance layered on <c>CLAUDE.md</c>.</summary>
    public string ConventionsMarkdown { get; init; } = string.Empty;

    /// <summary><c>primer.md</c> — the requester-facing "how this app is put together".</summary>
    public string PrimerMarkdown { get; init; } = string.Empty;

    /// <summary><c>glossary.yml</c> — domain term to plain English.</summary>
    public GlossaryDocument Glossary { get; init; } = GlossaryDocument.Empty;

    /// <summary><c>templates/</c> — request templates.</summary>
    public IReadOnlyList<CharterTemplate> Templates { get; init; } = [];

    /// <summary><c>checks/</c> plus the inline <c>checks:</c> block of <c>config.yml</c>.</summary>
    public IReadOnlyList<CharterCheck> Checks { get; init; } = [];

    /// <summary><c>policies/migrations.yml</c> (section 15).</summary>
    public MigrationPolicyDocument Migrations { get; init; } = MigrationPolicyDocument.Default;

    /// <summary>
    /// Everything that was odd but survivable: unknown keys, unreadable files, a missing folder.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>A folder that is not there. Deny by default, and one warning saying so.</summary>
    public static CharterFolder Missing(string commitSha) => new()
    {
        Exists = false,
        CommitSha = commitSha,
        Warnings =
        [
            "This repository has no .charter/ folder. Until one is committed it has no scopes, no "
            + "checks and no glossary, so Charter will refuse every request against it (section 7.3).",
        ],
    };

    /// <summary>
    /// Projects the folder into the context refinement is grounded in (sections 8, 10, 26.3).
    /// </summary>
    /// <param name="standards">
    /// The organisation's <c>standards.yml</c> (section 26.3), which lives in the standards repo
    /// rather than this one and is therefore passed in.
    /// </param>
    /// <param name="repositoryFullName">
    /// <c>owner/name</c>, used only to derive a display name when the repository names none. Section
    /// 7.1: a requester never sees <c>owner/repo</c>, so this is a fallback, not the default.
    /// </param>
    public RefinementContext ToRefinementContext(
        StandardsDocument? standards = null,
        string? repositoryFullName = null)
        => new()
        {
            Glossary = Glossary,
            PrimerMarkdown = PrimerMarkdown,
            ConventionsMarkdown = ConventionsMarkdown,
            Standards = standards ?? StandardsDocument.None,
            Scope = new RefinementScopePolicy(Config.Allow, Config.Deny),
            ProjectName = Config.ProjectName
                          ?? (repositoryFullName is { Length: > 0 } fullName
                              ? Humanise(fullName)
                              : "this project"),
        };

    /// <summary>
    /// The jsonb snapshot stored on the repository row (section 5, <c>charter_config_snapshot</c>).
    /// </summary>
    /// <remarks>
    /// A snapshot, not the source of truth: the committed file is, because changing a guardrail must
    /// require a pull request. The shape mirrors the YAML so the authoriser's restriction reader and
    /// the API's project profile — both of which read this column — see the same key names an
    /// engineer would read in the file.
    /// </remarks>
    public string ToSnapshotJson()
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Config.Version);

            WriteIfPresent(writer, "base_branch", Config.BaseBranch);
            WriteIfPresent(writer, "runner_image", Config.RunnerImage);
            WriteIfPresent(writer, "seed", Config.Seed);
            WriteIfPresent(writer, "name", Config.ProjectName);
            WriteIfPresent(writer, "description", Config.Description);

            writer.WriteStartObject("scopes");
            WriteArray(writer, "allow", Config.Allow);
            WriteArray(writer, "deny", Config.Deny);
            writer.WriteEndObject();

            writer.WriteStartArray("checks");
            foreach (var check in Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", check.Name);
                writer.WriteString("run", check.Run);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (!Config.Limits.IsEmpty)
            {
                writer.WriteStartObject("limits");

                if (Config.Limits.MaxSessionUsd is { } usd)
                {
                    writer.WriteNumber("max_session_usd", usd);
                }

                if (Config.Limits.MaxFilesChanged is { } files)
                {
                    writer.WriteNumber("max_files_changed", files);
                }

                writer.WriteEndObject();
            }

            if (Templates.Count > 0)
            {
                writer.WriteStartArray("templates");
                foreach (var template in Templates)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", template.Id);
                    writer.WriteString("name", template.Name);
                    writer.WriteString("description", template.Description);
                    writer.WriteString("prompt", template.Prompt);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (!Glossary.IsEmpty)
            {
                writer.WriteStartObject("glossary");
                foreach (var (term, definition) in Glossary.Terms)
                {
                    writer.WriteString(term, definition);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Builds a folder from already-read file contents, keyed by repository-relative path.</summary>
    /// <remarks>
    /// Separated from the GitHub read so the parsing rules can be tested against a dictionary of
    /// strings with no HTTP anywhere near them.
    /// </remarks>
    public static CharterFolder FromFiles(IReadOnlyDictionary<string, string> files, string commitSha)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        var charterFiles = files
            .Where(entry => entry.Key.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !entry.Key.StartsWith(CacheDirectory, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        if (charterFiles.Count == 0)
        {
            return Missing(commitSha);
        }

        var warnings = new List<string>();

        var config = charterFiles.TryGetValue(".charter/config.yml", out var configYaml)
                     || charterFiles.TryGetValue(".charter/config.yaml", out configYaml)
            ? CharterConfigDocument.Parse(configYaml, warnings)
            : Warn(warnings, ".charter/config.yml is missing, so this repository has no scopes and no "
                             + "requester may file against it (section 7.3).");

        var glossary = charterFiles.TryGetValue(".charter/glossary.yml", out var glossaryYaml)
                       || charterFiles.TryGetValue(".charter/glossary.yaml", out glossaryYaml)
            ? GlossaryDocument.Parse(glossaryYaml, warnings)
            : GlossaryDocument.Empty;

        var migrations = charterFiles.TryGetValue(".charter/policies/migrations.yml", out var migrationsYaml)
                         || charterFiles.TryGetValue(".charter/policies/migrations.yaml", out migrationsYaml)
            ? MigrationPolicyDocument.Parse(migrationsYaml, warnings)
            : MigrationPolicyDocument.Default;

        return new CharterFolder
        {
            Exists = true,
            CommitSha = commitSha,
            Config = config,
            ConventionsMarkdown = Read(charterFiles, ".charter/conventions.md"),
            PrimerMarkdown = Read(charterFiles, ".charter/primer.md"),
            Glossary = glossary,
            Templates = ReadTemplates(charterFiles, warnings),
            Checks = MergeChecks(config.Checks, ReadChecks(charterFiles, warnings)),
            Migrations = migrations,
            Warnings = warnings,
        };
    }

    private static CharterConfigDocument Warn(ICollection<string> warnings, string message)
    {
        warnings.Add(message);
        return CharterConfigDocument.Empty;
    }

    private static string Read(IReadOnlyDictionary<string, string> files, string path)
        => files.TryGetValue(path, out var text) ? text.Trim() : string.Empty;

    private static IReadOnlyList<CharterTemplate> ReadTemplates(
        IReadOnlyDictionary<string, string> files,
        ICollection<string> warnings)
    {
        var templates = new List<CharterTemplate>();

        foreach (var (path, content) in files.Where(entry =>
                     entry.Key.StartsWith(".charter/templates/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var id = Stem(path);

            if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseTemplateYaml(id, path, content, warnings) is { } parsed)
                {
                    templates.Add(parsed);
                }

                continue;
            }

            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                templates.Add(ParseTemplateMarkdown(id, content));
                continue;
            }

            warnings.Add($"{path} is not a .yml or .md template and was ignored.");
        }

        return templates;
    }

    private static CharterTemplate? ParseTemplateYaml(
        string id,
        string path,
        string content,
        ICollection<string> warnings)
    {
        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(content));
        }
        catch (YamlException ex)
        {
            warnings.Add($"{path} is not valid YAML and was ignored: {ex.Message}");
            return null;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings.Add($"{path} is not a YAML mapping and was ignored.");
            return null;
        }

        foreach (var (keyNode, _) in root.Children)
        {
            if (keyNode is YamlScalarNode { Value: { Length: > 0 } key }
                && key is not ("version" or "id" or "name" or "description" or "prompt" or "fields" or "icon"))
            {
                warnings.Add($"{path}: unknown key '{key}' was ignored.");
            }
        }

        return new CharterTemplate
        {
            Id = CharterConfigDocument.Child(root, "id") ?? id,
            Name = CharterConfigDocument.Child(root, "name") ?? Humanise(id),
            Description = CharterConfigDocument.Child(root, "description") ?? string.Empty,
            Prompt = CharterConfigDocument.Child(root, "prompt") ?? string.Empty,
        };
    }

    /// <summary>
    /// A Markdown template: the first <c># heading</c> is its name, the rest is the prompt.
    /// </summary>
    private static CharterTemplate ParseTemplateMarkdown(string id, string content)
    {
        var text = content.Trim();
        var name = Humanise(id);
        var body = text;

        if (text.StartsWith("# ", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            name = (newline < 0 ? text[2..] : text[2..newline]).Trim();
            body = newline < 0 ? string.Empty : text[(newline + 1)..].Trim();
        }

        return new CharterTemplate { Id = id, Name = name, Prompt = body };
    }

    /// <summary>
    /// Reads <c>checks/</c>.
    /// </summary>
    /// <remarks>
    /// A <c>.yml</c> file declares <c>name</c> and <c>run</c>. Anything else is treated as a script
    /// to execute by path, which is the shape an operator reaches for when the check is more than one
    /// line. The file's <em>contents</em> are never spliced into a command line: the runner executes
    /// the committed path, so what runs is what a reviewer read.
    /// </remarks>
    private static IReadOnlyList<CharterCheck> ReadChecks(
        IReadOnlyDictionary<string, string> files,
        ICollection<string> warnings)
    {
        var checks = new List<CharterCheck>();

        foreach (var (path, content) in files.Where(entry =>
                     entry.Key.StartsWith(".charter/checks/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var id = Stem(path);

            if (!path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new CharterCheck(id, path));
                continue;
            }

            var stream = new YamlStream();

            try
            {
                stream.Load(new StringReader(content));
            }
            catch (YamlException ex)
            {
                warnings.Add($"{path} is not valid YAML and was ignored: {ex.Message}");
                continue;
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                warnings.Add($"{path} is not a YAML mapping and was ignored.");
                continue;
            }

            var run = CharterConfigDocument.Child(root, "run");

            if (run is null)
            {
                warnings.Add($"{path} declares no 'run:' command and was ignored.");
                continue;
            }

            checks.Add(new CharterCheck(CharterConfigDocument.Child(root, "name") ?? id, run));
        }

        return checks;
    }

    /// <summary>Inline checks win; a file of the same name does not silently shadow one.</summary>
    private static IReadOnlyList<CharterCheck> MergeChecks(
        IReadOnlyList<CharterCheck> inline,
        IReadOnlyList<CharterCheck> fromFiles)
    {
        var merged = new List<CharterCheck>(inline);

        foreach (var check in fromFiles)
        {
            if (!merged.Any(existing => string.Equals(existing.Name, check.Name, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(check);
            }
        }

        return merged;
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Stem(string path)
    {
        var slash = path.LastIndexOf('/');
        var file = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = file.LastIndexOf('.');

        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>Turns <c>northbeam/quote-tool</c> or <c>copy-change</c> into readable text.</summary>
    private static string Humanise(string value)
    {
        var slash = value.LastIndexOf('/');
        var segment = slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
        var spaced = segment.Replace('-', ' ').Replace('_', ' ').Trim();

        return spaced.Length == 0
            ? value
            : string.Concat(
                spaced[..1].ToUpper(CultureInfo.InvariantCulture),
                spaced[1..].ToLower(CultureInfo.InvariantCulture));
    }
}
