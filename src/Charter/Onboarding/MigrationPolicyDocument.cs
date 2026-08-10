using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Onboarding;

/// <summary>The three classes of section 15, in ascending severity.</summary>
public enum MigrationClass
{
    /// <summary>New table, nullable column, index. Flows normally; the PR is labelled <c>schema-change</c>.</summary>
    Additive,

    /// <summary>Rename, type change, non-null with a default. Engineer review required.</summary>
    Ambiguous,

    /// <summary>Drop, truncate, non-null without a default. The session halts.</summary>
    Destructive,
}

/// <summary>
/// <c>.charter/policies/migrations.yml</c> — the destructive-operation rules of section 15.
/// </summary>
/// <remarks>
/// <para>
/// Section 15 classifies rather than blanket-gates, and says to classify <em>structurally</em> by
/// inspecting the generated migration's operations. This document is the configurable half of that:
/// the operation-name to class mapping. The structural inspection itself belongs to whatever reads a
/// generated migration, and takes this as its rule table.
/// </para>
/// <para>
/// The defaults are section 15's own table, so a repository that never writes this file still gets
/// the specified behaviour. A repository that does write one <em>adds</em> to the defaults rather
/// than replacing them, so a file listing one extra destructive operation does not silently
/// reclassify <c>drop_table</c> as unknown.
/// </para>
/// </remarks>
public sealed record MigrationPolicyDocument
{
    private static readonly string[] DefaultAdditive =
    [
        "create_table", "add_column", "create_index", "add_foreign_key", "add_check_constraint",
        "create_sequence", "create_schema",
    ];

    private static readonly string[] DefaultAmbiguous =
    [
        "rename_table", "rename_column", "alter_column", "alter_column_type", "add_column_not_null_with_default",
        "rename_index", "alter_sequence",
    ];

    private static readonly string[] DefaultDestructive =
    [
        "drop_table", "drop_column", "drop_index", "drop_foreign_key", "drop_schema", "drop_sequence",
        "truncate", "add_column_not_null_without_default", "delete_data", "sql",
    ];

    /// <summary>Section 15's own table, for a repository that has not written the file.</summary>
    public static MigrationPolicyDocument Default { get; } = new();

    /// <summary>The <c>version</c> key.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Whether the repository actually committed a policy file.</summary>
    public bool IsDeclared { get; init; }

    /// <summary>Extra operations the repository classifies as additive.</summary>
    public IReadOnlyList<string> Additive { get; init; } = [];

    /// <summary>Extra operations the repository classifies as ambiguous.</summary>
    public IReadOnlyList<string> Ambiguous { get; init; } = [];

    /// <summary>Extra operations the repository classifies as destructive.</summary>
    public IReadOnlyList<string> Destructive { get; init; } = [];

    /// <summary>
    /// Classifies one migration operation by name.
    /// </summary>
    /// <remarks>
    /// Unknown operations are <see cref="MigrationClass.Ambiguous"/>, not additive. An operation this
    /// version has never seen is exactly the case where a human should look, and defaulting the other
    /// way would let a future EF operation flow through unreviewed.
    /// </remarks>
    public MigrationClass Classify(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var name = Normalise(operation);

        if (Matches(Destructive, name) || Matches(DefaultDestructive, name))
        {
            return MigrationClass.Destructive;
        }

        if (Matches(Ambiguous, name) || Matches(DefaultAmbiguous, name))
        {
            return MigrationClass.Ambiguous;
        }

        return Matches(Additive, name) || Matches(DefaultAdditive, name)
            ? MigrationClass.Additive
            : MigrationClass.Ambiguous;
    }

    /// <summary>Parses the policy file. Unknown keys warn, never fail (section 8).</summary>
    public static MigrationPolicyDocument Parse(string yaml, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Default;
        }

        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            warnings?.Add($".charter/policies/migrations.yml is not valid YAML and was ignored: {ex.Message}");
            return Default;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings?.Add(".charter/policies/migrations.yml is not a YAML mapping and was ignored.");
            return Default;
        }

        var version = 1;
        var declaredVersion = false;
        IReadOnlyList<string> additive = [];
        IReadOnlyList<string> ambiguous = [];
        IReadOnlyList<string> destructive = [];

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
                    if (CharterConfigDocument.TryInt(valueNode, out var parsed))
                    {
                        version = parsed;
                    }

                    break;

                case "additive":
                    additive = CharterConfigDocument.Strings(valueNode);
                    break;

                case "ambiguous":
                    ambiguous = CharterConfigDocument.Strings(valueNode);
                    break;

                case "destructive":
                    destructive = CharterConfigDocument.Strings(valueNode);
                    break;

                default:
                    warnings?.Add($".charter/policies/migrations.yml: unknown key '{key}' was ignored.");
                    break;
            }
        }

        if (!declaredVersion)
        {
            warnings?.Add(
                ".charter/policies/migrations.yml has no 'version:' key; assuming version 1.");
        }

        return new MigrationPolicyDocument
        {
            Version = version,
            IsDeclared = true,
            Additive = additive,
            Ambiguous = ambiguous,
            Destructive = destructive,
        };
    }

    private static bool Matches(IReadOnlyList<string> names, string candidate)
    {
        foreach (var name in names)
        {
            if (string.Equals(Normalise(name), candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Folds <c>DropColumn</c>, <c>drop-column</c> and <c>drop_column</c> together.
    /// </summary>
    /// <remarks>
    /// EF Core spells its operations in Pascal case and a hand-written policy file will not, and
    /// making an operator guess which is a security control that fails silently.
    /// </remarks>
    private static string Normalise(string value)
        => value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("operation", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant()
            .Trim();
}
