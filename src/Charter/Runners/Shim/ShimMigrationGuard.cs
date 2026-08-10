using Charter.Runners.SchemaChanges;

namespace Charter.Runners.Shim;

/// <summary>A migration the agent wrote, and what section 15 makes of it.</summary>
public sealed record ShimMigrationFinding(string Path, MigrationClassification Classification);

/// <summary>
/// Watches the agent's writes for generated EF migrations and applies section 15 to them.
/// </summary>
/// <remarks>
/// <para>
/// The risk section 15 manages is not data loss during a session — preview databases are disposable —
/// but a bad migration <em>merging</em>. So the guard classifies rather than blanket-gates: additive
/// flows, ambiguous is recorded for the engineer, and destructive stops the run so a human writes the
/// migration by hand.
/// </para>
/// <para>
/// It lives in the shim for the same reason path scope does. The classification has to happen against
/// the file the agent actually wrote, in the sandbox, while there is still a run to stop — not later,
/// against a diff, once the work is finished and the tokens are spent.
/// </para>
/// </remarks>
public sealed class ShimMigrationGuard
{
    private readonly string _workspaceRoot;
    private readonly MigrationPolicy _policy;
    private readonly Func<string, string?> _readFile;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public ShimMigrationGuard(
        string workspaceRoot,
        MigrationPolicy? policy = null,
        Func<string, string?>? readFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _workspaceRoot = workspaceRoot;
        _policy = policy ?? MigrationPolicy.Default;
        _readFile = readFile ?? ReadIfPresent;
    }

    /// <summary>Every migration seen so far, in the order they were written.</summary>
    public List<ShimMigrationFinding> Findings { get; } = [];

    /// <summary>Loads the repository's own rules from <c>.charter/policies/migrations.yml</c>.</summary>
    public static ShimMigrationGuard ForRepository(string workspaceRoot, ICollection<string> warnings)
        => new(workspaceRoot, MigrationPolicy.Load(workspaceRoot, warnings));

    /// <summary>
    /// Classifies a written file if it is a migration. Returns the finding when the session must
    /// stop, and <see langword="null"/> otherwise.
    /// </summary>
    public ShimMigrationFinding? Inspect(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (!IsGeneratedMigration(relativePath) || !_seen.Add(relativePath))
        {
            return null;
        }

        var source = _readFile(Path.Combine(_workspaceRoot, relativePath));
        if (source is null)
        {
            return null;
        }

        var finding = new ShimMigrationFinding(relativePath, MigrationClassifier.Classify(source, _policy));
        Findings.Add(finding);

        return finding.Classification.HaltsSession ? finding : null;
    }

    /// <summary>
    /// True for a hand-editable EF migration.
    /// </summary>
    /// <remarks>
    /// The designer file and the model snapshot are generated companions with no <c>Up</c> method
    /// worth reading; classifying them would produce a second, meaningless verdict for every real
    /// migration.
    /// </remarks>
    public static bool IsGeneratedMigration(string relativePath)
    {
        var path = ShimGlob.NormalizePath(relativePath);

        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Split('/').Any(segment => segment.Equals("migrations", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
