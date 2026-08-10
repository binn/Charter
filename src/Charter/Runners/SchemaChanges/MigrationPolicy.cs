using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Runners.SchemaChanges;

/// <summary>The three classes of section 15.</summary>
public enum MigrationClass
{
    /// <summary>New table, nullable column, index. Flows normally; the PR is labelled <c>schema-change</c>.</summary>
    Additive,

    /// <summary>Rename, type change, non-null with a default. Engineer review; the PR is blocked.</summary>
    Ambiguous,

    /// <summary>
    /// Drop, truncate, non-null without a default. <strong>The session halts</strong> and an engineer
    /// authors the migration by hand.
    /// </summary>
    Destructive,
}

/// <summary>What the classification means for the session, in section 15's own words.</summary>
public enum MigrationOutcome
{
    /// <summary>Continue. The pull request carries the <c>schema-change</c> label.</summary>
    Flows,

    /// <summary>Continue, but the pull request is blocked until an engineer approves the migration.</summary>
    RequiresReview,

    /// <summary>Stop. The agent writes the intent; a human writes the migration.</summary>
    HaltsSession,
}

/// <summary>
/// Which operations count as what, and the two column rules that are not about the operation name
/// (section 15). Configurable from <c>.charter/policies/migrations.yml</c>.
/// </summary>
public sealed record MigrationPolicy
{
    /// <summary>Where the repository's overrides live (section 8).</summary>
    public const string ConfigPath = ".charter/policies/migrations.yml";

    /// <summary>The only schema version this Charter understands (section 8).</summary>
    public const int SupportedVersion = 1;

    /// <summary>Operation name (normalised) to class. Anything absent uses <see cref="UnknownOperation"/>.</summary>
    public required IReadOnlyDictionary<string, MigrationClass> Operations { get; init; }

    /// <summary>Adding or altering a column to non-null with no default. Section 15: destructive.</summary>
    public MigrationClass NonNullWithoutDefault { get; init; } = MigrationClass.Destructive;

    /// <summary>Adding or altering a column to non-null with a default. Section 15: ambiguous.</summary>
    public MigrationClass NonNullWithDefault { get; init; } = MigrationClass.Ambiguous;

    /// <summary>
    /// An operation this Charter has never heard of.
    /// </summary>
    /// <remarks>
    /// Ambiguous, not additive. A newer EF, or a hand-written operation, must land in front of an
    /// engineer rather than flow through on the strength of Charter not recognising it.
    /// </remarks>
    public MigrationClass UnknownOperation { get; init; } = MigrationClass.Ambiguous;

    /// <summary>Section 15's table, as shipped.</summary>
    public static MigrationPolicy Default { get; } = new()
    {
        Operations = BuildDefaults(),
    };

    /// <summary>The class for one operation name, applying <see cref="UnknownOperation"/> as needed.</summary>
    public MigrationClass ClassOf(string operationName)
    {
        ArgumentNullException.ThrowIfNull(operationName);

        return Operations.TryGetValue(Normalize(operationName), out var known) ? known : UnknownOperation;
    }

    /// <summary>What section 15 does about a class.</summary>
    public static MigrationOutcome OutcomeOf(MigrationClass migrationClass) => migrationClass switch
    {
        MigrationClass.Additive => MigrationOutcome.Flows,
        MigrationClass.Ambiguous => MigrationOutcome.RequiresReview,
        _ => MigrationOutcome.HaltsSession,
    };

    /// <summary>
    /// Loads <c>.charter/policies/migrations.yml</c> over the defaults.
    /// </summary>
    /// <remarks>
    /// Section 8's extensibility rules apply: <c>version: 1</c> is required, and unknown keys warn
    /// rather than fail, so a repository written for a newer Charter still loads here. A value that is
    /// not one of the three class names is a defect in the file and warns loudly while leaving the
    /// stricter default in place — a typo must never quietly loosen a guardrail.
    /// </remarks>
    public static MigrationPolicy Parse(string yaml, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(warnings);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            warnings.Add($"{ConfigPath} is not valid YAML ({exception.Message}); the shipped rules are in force.");
            return Default;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings.Add($"{ConfigPath} is empty or is not a mapping; the shipped rules are in force.");
            return Default;
        }

        var version = Scalar(root, "version");
        if (version is null)
        {
            warnings.Add($"{ConfigPath} has no 'version: {SupportedVersion}'; the shipped rules are in force.");
            return Default;
        }

        if (!int.TryParse(version, System.Globalization.CultureInfo.InvariantCulture, out var parsedVersion)
            || parsedVersion != SupportedVersion)
        {
            warnings.Add(
                $"{ConfigPath} declares version '{version}', and this Charter understands version "
                + $"{SupportedVersion}; the shipped rules are in force.");
            return Default;
        }

        var operations = new Dictionary<string, MigrationClass>(Default.Operations, StringComparer.Ordinal);
        var policy = Default with { Operations = operations };

        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;

            switch (key)
            {
                case "version":
                    break;

                case "operations" when valueNode is YamlMappingNode operationMap:
                    ReadOperations(operationMap, operations, warnings);
                    break;

                case "non_null_without_default":
                    policy = policy with
                    {
                        NonNullWithoutDefault = ReadClass(valueNode, key, policy.NonNullWithoutDefault, warnings),
                    };
                    break;

                case "non_null_with_default":
                    policy = policy with
                    {
                        NonNullWithDefault = ReadClass(valueNode, key, policy.NonNullWithDefault, warnings),
                    };
                    break;

                case "unknown_operation":
                    policy = policy with
                    {
                        UnknownOperation = ReadClass(valueNode, key, policy.UnknownOperation, warnings),
                    };
                    break;

                default:
                    warnings.Add(
                        $"{ConfigPath}: '{key}' is not a key this Charter understands and was ignored.");
                    break;
            }
        }

        return policy;
    }

    /// <summary>Reads the policy from a repository checkout, falling back to the shipped rules.</summary>
    public static MigrationPolicy Load(string repositoryRoot, ICollection<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(warnings);

        var path = Path.Combine(repositoryRoot, ConfigPath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? Parse(File.ReadAllText(path), warnings) : Default;
    }

    /// <summary>Folds <c>DropColumn</c>, <c>drop_column</c> and <c>dropcolumn</c> onto one key.</summary>
    public static string Normalize(string operationName)
        => operationName.Replace("_", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static void ReadOperations(
        YamlMappingNode map,
        Dictionary<string, MigrationClass> operations,
        ICollection<string> warnings)
    {
        foreach (var (keyNode, valueNode) in map.Children)
        {
            var name = (keyNode as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalized = Normalize(name);
            var current = operations.TryGetValue(normalized, out var existing) ? existing : MigrationClass.Ambiguous;
            operations[normalized] = ReadClass(valueNode, $"operations.{name}", current, warnings);
        }
    }

    private static MigrationClass ReadClass(
        YamlNode node,
        string key,
        MigrationClass fallback,
        ICollection<string> warnings)
    {
        var value = (node as YamlScalarNode)?.Value?.Trim().ToLowerInvariant();

        return value switch
        {
            "additive" => MigrationClass.Additive,
            "ambiguous" => MigrationClass.Ambiguous,
            "destructive" => MigrationClass.Destructive,
            _ => Reject(value),
        };

        MigrationClass Reject(string? rejected)
        {
            warnings.Add(
                $"{ConfigPath}: '{key}' is '{rejected}', which is not one of additive, ambiguous or "
                + $"destructive. Keeping '{fallback.ToString().ToLowerInvariant()}'.");
            return fallback;
        }
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return (valueNode as YamlScalarNode)?.Value;
            }
        }

        return null;
    }

    private static Dictionary<string, MigrationClass> BuildDefaults()
    {
        var map = new Dictionary<string, MigrationClass>(StringComparer.Ordinal);

        // Additive: new table, index, sequence, schema, seed data. Nothing here can lose a row.
        foreach (var name in new[]
                 {
                     "CreateTable", "CreateIndex", "CreateSequence", "CreateSchema", "InsertData",
                     "EnsureSchema",
                 })
        {
            map[Normalize(name)] = MigrationClass.Additive;
        }

        // Ambiguous: renames, type changes, constraints that can fail against existing rows, and raw
        // SQL — which Charter deliberately does not read for keywords, because section 15 says to
        // classify structurally and a grep for DROP is exactly the heuristic it rules out.
        foreach (var name in new[]
                 {
                     "AddColumn", "AlterColumn", "AlterTable", "AlterDatabase", "AlterSequence",
                     "RenameColumn", "RenameTable", "RenameIndex", "RenameSequence",
                     "AddForeignKey", "AddPrimaryKey", "AddUniqueConstraint", "AddCheckConstraint",
                     "DropForeignKey", "DropIndex", "DropPrimaryKey", "DropUniqueConstraint",
                     "DropCheckConstraint", "UpdateData", "Sql", "RestartSequence",
                 })
        {
            map[Normalize(name)] = MigrationClass.Ambiguous;
        }

        // Destructive: the session halts and an engineer writes it by hand.
        foreach (var name in new[]
                 {
                     "DropColumn", "DropTable", "DropSchema", "DropSequence", "DeleteData",
                 })
        {
            map[Normalize(name)] = MigrationClass.Destructive;
        }

        return map;
    }
}
