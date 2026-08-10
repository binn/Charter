using System.Globalization;
using System.Text;
using System.Text.Json;
using Charter.Refinement;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Onboarding;

/// <summary>One named validation command the agent must pass (section 8, <c>checks/</c>).</summary>
/// <param name="Name">The check's name, as the recap and the status thread refer to it.</param>
/// <param name="Run">The command line to run.</param>
public sealed record CharterCheck(string Name, string Run);

/// <summary>A request template (section 8, <c>templates/</c>).</summary>
/// <remarks>
/// The cheapest quality win available: a requester picking "change some text" instead of free-typing
/// skips half the refinement round-trips.
/// </remarks>
public sealed record CharterTemplate
{
    /// <summary>Stable id, taken from the file name when the file does not name one.</summary>
    public required string Id { get; init; }

    /// <summary>What the requester sees on the button.</summary>
    public required string Name { get; init; }

    /// <summary>One line under the name.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The starting text the template drops into the box.</summary>
    public string Prompt { get; init; } = string.Empty;
}

/// <summary>The <c>limits</c> block of <c>config.yml</c>.</summary>
public sealed record CharterLimits
{
    /// <summary>Nothing configured.</summary>
    public static CharterLimits None { get; } = new();

    /// <summary>Ceiling on one session's spend (section 7.5).</summary>
    public decimal? MaxSessionUsd { get; init; }

    /// <summary>Ceiling on how many files one session may touch.</summary>
    public int? MaxFilesChanged { get; init; }

    /// <summary>Whether either limit was set.</summary>
    public bool IsEmpty => MaxSessionUsd is null && MaxFilesChanged is null;
}

/// <summary>
/// A parsed <c>.charter/config.yml</c> (section 8).
/// </summary>
/// <remarks>
/// <para>
/// The extensibility rules of section 8 are the whole character of this parser: <c>version: 1</c> is
/// expected at the top, and <strong>unknown keys warn and are ignored, never fail</strong>. A repo
/// written for a newer Charter must keep working on an older one, so every unrecognised key produces
/// a line in <see cref="CharterFolder.Warnings"/> and nothing else.
/// </para>
/// <para>
/// The same rule applies one level down. A <c>limits</c> block naming a limit this version has never
/// heard of is a warning about that key, not a refusal to read the two limits it does understand.
/// </para>
/// </remarks>
public sealed record CharterConfigDocument
{
    private static readonly string[] KnownKeys =
    [
        "version", "base_branch", "runner_image", "seed", "scopes", "checks", "limits",
        "project", "name", "display_name", "description", "templates", "glossary",
        "auto_dispatch", "deviations", "scaffolding", "policies", "standards_version",
    ];

    /// <summary>A repository with no committed config: deny by default, everything unset.</summary>
    public static CharterConfigDocument Empty { get; } = new();

    /// <summary>The <c>version</c> key. Defaults to 1 when the file did not declare one.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Whether the file actually carried a <c>version</c> key.</summary>
    public bool DeclaredVersion { get; init; }

    /// <summary>The branch sessions branch from.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The prebuilt runner image this repo asks for (section 32.1).</summary>
    public string? RunnerImage { get; init; }

    /// <summary>The optional dev-seed command. Section 9: optional, and its absence warns rather than blocks.</summary>
    public string? Seed { get; init; }

    /// <summary>The requester-facing project name. Never <c>owner/repo</c> (section 7.1).</summary>
    public string? ProjectName { get; init; }

    /// <summary>One line about the project, for the project list.</summary>
    public string? Description { get; init; }

    /// <summary><c>scopes.allow</c>.</summary>
    public IReadOnlyList<string> Allow { get; init; } = [];

    /// <summary><c>scopes.deny</c>. Deny wins over allow.</summary>
    public IReadOnlyList<string> Deny { get; init; } = [];

    /// <summary><c>checks</c>, as declared inline in <c>config.yml</c>.</summary>
    public IReadOnlyList<CharterCheck> Checks { get; init; } = [];

    /// <summary><c>limits</c>.</summary>
    public CharterLimits Limits { get; init; } = CharterLimits.None;

    /// <summary>The <c>standards.yml</c> version this repo was created under (section 26.7).</summary>
    public int? StandardsVersion { get; init; }

    /// <summary>Parses <c>config.yml</c>, warning about anything it does not recognise.</summary>
    public static CharterConfigDocument Parse(string yaml, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            warnings?.Add(".charter/config.yml is empty; the repository is requestable by nobody.");
            return Empty;
        }

        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            // Failing closed, loudly. An unreadable guardrail file must not read as "no guardrails".
            warnings?.Add($".charter/config.yml is not valid YAML and was ignored: {ex.Message}");
            return Empty;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings?.Add(".charter/config.yml is not a YAML mapping and was ignored.");
            return Empty;
        }

        var version = 1;
        var declaredVersion = false;
        string? baseBranch = null;
        string? runnerImage = null;
        string? seed = null;
        string? projectName = null;
        string? description = null;
        int? standardsVersion = null;
        IReadOnlyList<string> allow = [];
        IReadOnlyList<string> deny = [];
        IReadOnlyList<CharterCheck> checks = [];
        var limits = CharterLimits.None;

        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            switch (key)
            {
                case "version":
                    declaredVersion = true;
                    if (TryInt(valueNode, out var parsedVersion))
                    {
                        version = parsedVersion;
                    }

                    break;

                case "base_branch":
                    baseBranch = Scalar(valueNode);
                    break;

                case "runner_image":
                    runnerImage = Scalar(valueNode);
                    break;

                case "seed":
                    seed = Scalar(valueNode);
                    break;

                case "description":
                    description = Scalar(valueNode);
                    break;

                case "name":
                case "display_name":
                    projectName ??= Scalar(valueNode);
                    break;

                case "standards_version":
                    standardsVersion = TryInt(valueNode, out var standards) ? standards : null;
                    break;

                case "project":
                    if (valueNode is YamlMappingNode project)
                    {
                        projectName = Child(project, "name") ?? Child(project, "display_name") ?? projectName;
                        description ??= Child(project, "description");
                    }

                    break;

                case "scopes":
                    (allow, deny) = ReadScopes(valueNode, warnings);
                    break;

                case "checks":
                    checks = ReadChecks(valueNode, warnings);
                    break;

                case "limits":
                    limits = ReadLimits(valueNode, warnings);
                    break;

                case "templates":
                case "glossary":
                case "auto_dispatch":
                case "deviations":
                case "scaffolding":
                case "policies":
                    // Read elsewhere — by the API's project profile, the authoriser's restriction
                    // reader, or a sibling file. Known, so not a warning.
                    break;

                default:
                    warnings?.Add(
                        $".charter/config.yml: unknown key '{key}' was ignored. This Charter may be "
                        + "older than the file.");
                    break;
            }
        }

        if (!declaredVersion)
        {
            warnings?.Add(
                ".charter/config.yml has no 'version:' key. Section 8 expects `version: 1` at the top "
                + "of every Charter YAML file; assuming version 1.");
        }
        else if (version > 1)
        {
            warnings?.Add(
                $".charter/config.yml declares version {version.ToString(CultureInfo.InvariantCulture)}, "
                + "which is newer than this Charter understands. Keys it does not recognise were ignored.");
        }

        return new CharterConfigDocument
        {
            Version = version,
            DeclaredVersion = declaredVersion,
            BaseBranch = baseBranch,
            RunnerImage = runnerImage,
            Seed = seed,
            ProjectName = projectName,
            Description = description,
            Allow = allow,
            Deny = deny,
            Checks = checks,
            Limits = limits,
            StandardsVersion = standardsVersion,
        };
    }

    private static (IReadOnlyList<string> Allow, IReadOnlyList<string> Deny) ReadScopes(
        YamlNode node,
        ICollection<string>? warnings)
    {
        if (node is not YamlMappingNode mapping)
        {
            warnings?.Add(".charter/config.yml: 'scopes' is not a mapping of allow/deny and was ignored.");
            return ([], []);
        }

        List<string> allow = [];
        List<string> deny = [];

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            switch (key)
            {
                case "allow":
                    allow = Strings(valueNode);
                    break;

                case "deny":
                    deny = Strings(valueNode);
                    break;

                default:
                    warnings?.Add($".charter/config.yml: unknown key 'scopes.{key}' was ignored.");
                    break;
            }
        }

        return (allow, deny);
    }

    private static IReadOnlyList<CharterCheck> ReadChecks(YamlNode node, ICollection<string>? warnings)
    {
        if (node is not YamlSequenceNode sequence)
        {
            warnings?.Add(".charter/config.yml: 'checks' is not a list and was ignored.");
            return [];
        }

        var checks = new List<CharterCheck>();

        foreach (var child in sequence.Children)
        {
            if (child is not YamlMappingNode entry)
            {
                continue;
            }

            var name = Child(entry, "name");
            var run = Child(entry, "run");

            if (name is null || run is null)
            {
                warnings?.Add(".charter/config.yml: a 'checks' entry without both name and run was ignored.");
                continue;
            }

            checks.Add(new CharterCheck(name, run));
        }

        return checks;
    }

    private static CharterLimits ReadLimits(YamlNode node, ICollection<string>? warnings)
    {
        if (node is not YamlMappingNode mapping)
        {
            warnings?.Add(".charter/config.yml: 'limits' is not a mapping and was ignored.");
            return CharterLimits.None;
        }

        decimal? maxSessionUsd = null;
        int? maxFilesChanged = null;

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            switch (key)
            {
                case "max_session_usd":
                    if (valueNode is YamlScalarNode { Value: { Length: > 0 } usd }
                        && decimal.TryParse(usd, CultureInfo.InvariantCulture, out var parsedUsd))
                    {
                        maxSessionUsd = parsedUsd;
                    }

                    break;

                case "max_files_changed":
                    if (TryInt(valueNode, out var files))
                    {
                        maxFilesChanged = files;
                    }

                    break;

                default:
                    warnings?.Add($".charter/config.yml: unknown key 'limits.{key}' was ignored.");
                    break;
            }
        }

        return new CharterLimits { MaxSessionUsd = maxSessionUsd, MaxFilesChanged = maxFilesChanged };
    }

    internal static string? Child(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? Scalar(value) : null;

    internal static string? Scalar(YamlNode node)
        => node is YamlScalarNode { Value: { Length: > 0 } value } && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    internal static List<string> Strings(YamlNode node) => node switch
    {
        YamlSequenceNode sequence =>
        [
            .. sequence.Children
                .OfType<YamlScalarNode>()
                .Select(static child => child.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim()),
        ],
        YamlScalarNode { Value: { Length: > 0 } single } => [single.Trim()],
        _ => [],
    };

    internal static bool TryInt(YamlNode node, out int value)
    {
        value = 0;

        return node is YamlScalarNode { Value: { Length: > 0 } raw }
               && int.TryParse(raw, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Whether <paramref name="key"/> is a key this version reads.</summary>
    internal static bool IsKnownKey(string key) => KnownKeys.Contains(key, StringComparer.Ordinal);
}
