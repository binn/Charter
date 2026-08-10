namespace Charter.Runners.SchemaChanges;

/// <summary>One operation and what Charter made of it.</summary>
/// <param name="Operation">The EF operation name.</param>
/// <param name="Target">Table, column or index the operation names, when it names one.</param>
/// <param name="Class">Its class under the policy in force.</param>
/// <param name="Reason">Why, in a sentence an engineer can act on.</param>
public sealed record MigrationFinding(
    string Operation,
    string? Target,
    MigrationClass Class,
    string Reason);

/// <summary>The verdict on one migration file.</summary>
/// <param name="Class">The worst class found. An empty migration is additive.</param>
/// <param name="Outcome">What section 15 does about it.</param>
/// <param name="Findings">Every operation, in file order.</param>
/// <param name="Summary">The message the session, the pull request and the recap all use.</param>
public sealed record MigrationClassification(
    MigrationClass Class,
    MigrationOutcome Outcome,
    IReadOnlyList<MigrationFinding> Findings,
    string Summary)
{
    /// <summary>Section 15: the pull request carries this label whenever a migration is present.</summary>
    public const string SchemaChangeLabel = "schema-change";

    /// <summary>True when the session must stop and a human must author the migration.</summary>
    public bool HaltsSession => Outcome == MigrationOutcome.HaltsSession;

    /// <summary>The operations that produced the verdict, worst first.</summary>
    public IReadOnlyList<MigrationFinding> Worst
        => [.. Findings.Where(finding => finding.Class == Class)];
}

/// <summary>
/// Section 15, structurally.
/// </summary>
/// <remarks>
/// <para>
/// The risk being managed is not data loss during a session — preview databases are disposable — but
/// a bad migration <em>merging</em>. So this classifies rather than blanket-gates: additive flows,
/// ambiguous blocks the pull request until an engineer approves, and destructive halts the session so
/// the engineer writes the migration by hand.
/// </para>
/// <para>
/// Two rules do not follow from the operation name and are therefore applied on top of it. A column
/// added or altered to non-null <em>with</em> a default is ambiguous; the same change <em>without</em>
/// one is destructive, because it cannot apply to a table that already has rows. Both are configurable
/// in <c>.charter/policies/migrations.yml</c>, and both default to what section 15's table says.
/// </para>
/// </remarks>
public static class MigrationClassifier
{
    /// <summary>Classifies the <c>Up</c> operations of a generated migration file.</summary>
    /// <param name="source">The migration's C# source.</param>
    /// <param name="policy">The rules in force; <see cref="MigrationPolicy.Default"/> when null.</param>
    public static MigrationClassification Classify(string source, MigrationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rules = policy ?? MigrationPolicy.Default;

        if (!EfMigrationParser.TryParseUp(source, out var operations, out var problem))
        {
            // Unreadable is not the same as harmless. Section 15 requires structural inspection, and a
            // file that cannot be inspected gets the strictest treatment rather than the loosest.
            var finding = new MigrationFinding("(unparseable)", null, MigrationClass.Destructive, problem);

            return new MigrationClassification(
                MigrationClass.Destructive,
                MigrationOutcome.HaltsSession,
                [finding],
                problem);
        }

        return Classify(operations, rules);
    }

    /// <summary>Classifies an already-parsed operation list.</summary>
    public static MigrationClassification Classify(
        IReadOnlyList<MigrationOperation> operations,
        MigrationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var rules = policy ?? MigrationPolicy.Default;
        var findings = new List<MigrationFinding>(operations.Count);

        foreach (var operation in operations)
        {
            findings.Add(Classify(operation, rules));
        }

        var worst = findings.Count == 0
            ? MigrationClass.Additive
            : findings.Max(finding => finding.Class);

        return new MigrationClassification(
            worst,
            MigrationPolicy.OutcomeOf(worst),
            findings,
            Summarise(worst, findings));
    }

    private static MigrationFinding Classify(MigrationOperation operation, MigrationPolicy policy)
    {
        var target = EfMigrationParser.Describe(operation);

        if (IsColumnShapeChange(operation))
        {
            var nullable = operation.Nullable;

            if (nullable == false)
            {
                return operation.HasDefault
                    ? new MigrationFinding(
                        operation.Name,
                        target,
                        policy.NonNullWithDefault,
                        $"{target} makes the column non-null with a default. Existing rows take the default, "
                        + "which is a decision an engineer should see before it merges.")
                    : new MigrationFinding(
                        operation.Name,
                        target,
                        policy.NonNullWithoutDefault,
                        $"{target} makes the column non-null with no default. It cannot apply to a table "
                        + "that already has rows, so an engineer authors this migration by hand.");
            }

            if (nullable == true && operation.Name == "AddColumn")
            {
                return new MigrationFinding(
                    operation.Name,
                    target,
                    MigrationClass.Additive,
                    $"{target} adds a nullable column, which existing rows tolerate.");
            }
        }

        var classified = policy.ClassOf(operation.Name);
        var known = policy.Operations.ContainsKey(MigrationPolicy.Normalize(operation.Name));

        var reason = known
            ? $"{target} is classified {classified.ToString().ToLowerInvariant()} by the migration policy."
            : $"{target} is an operation this Charter does not model, so it is treated as "
              + $"{classified.ToString().ToLowerInvariant()} rather than assumed safe.";

        return new MigrationFinding(operation.Name, target, classified, reason);
    }

    /// <summary>Operations whose class depends on the resulting column shape, not on their name.</summary>
    private static bool IsColumnShapeChange(MigrationOperation operation)
        => operation.Name is "AddColumn" or "AlterColumn";

    private static string Summarise(MigrationClass worst, IReadOnlyList<MigrationFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "This migration has no schema operations.";
        }

        var culprits = findings.Where(finding => finding.Class == worst).ToArray();
        var named = string.Join("; ", culprits.Take(3).Select(finding => finding.Target ?? finding.Operation));
        var more = culprits.Length > 3 ? $" and {culprits.Length - 3} more" : string.Empty;

        return worst switch
        {
            MigrationClass.Additive =>
                $"This migration is additive ({named}{more}). It flows normally and the pull request is "
                + $"labelled '{MigrationClassification.SchemaChangeLabel}'.",

            MigrationClass.Ambiguous =>
                $"This migration needs an engineer to look at it ({named}{more}). The pull request is "
                + "blocked until it is approved.",

            _ =>
                $"This migration is destructive ({named}{more}). The session stops here: the agent has "
                + "written down what it intended, and an engineer authors the migration by hand.",
        };
    }
}
