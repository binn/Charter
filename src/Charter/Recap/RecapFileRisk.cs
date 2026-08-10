using Charter.Refinement;

namespace Charter.Recaps;

/// <summary>How a file arrived in the change.</summary>
public enum RecapFileChangeKind
{
    Added,

    Modified,

    Deleted,

    Renamed,
}

/// <summary>
/// One file the session touched, as the recap needs to see it.
/// </summary>
/// <param name="Path">Repository-relative, forward-slashed.</param>
/// <remarks>
/// Built either from the session's <c>file_write</c> events or from the provider's own comparison
/// (section 17). Either source is the session's real work; neither is a guess.
/// </remarks>
public sealed record RecapFileChange(string Path)
{
    /// <summary>Lines added, where the source reported them.</summary>
    public int LinesAdded { get; init; }

    /// <summary>Lines removed, where the source reported them.</summary>
    public int LinesRemoved { get; init; }

    /// <summary>How the file arrived.</summary>
    public RecapFileChangeKind Kind { get; init; } = RecapFileChangeKind.Modified;

    /// <summary>
    /// The change is whitespace, import ordering or a formatter run. Section 14 sinks these
    /// explicitly, and a caller that knows is better placed to say so than a path heuristic.
    /// </summary>
    public bool FormattingOnly { get; init; }

    /// <summary>Total lines touched, for tie-breaking within a risk band.</summary>
    public int LinesChanged => LinesAdded + LinesRemoved;
}

/// <summary>Why a file scored the way it did. Rendered beside the file, never as a verdict on it.</summary>
public enum RecapRiskFactor
{
    /// <summary>The path matches a pattern this repository denies the agent (section 8).</summary>
    Denylisted,

    /// <summary>The path sits beside a denied area — a sibling directory of one.</summary>
    DenylistAdjacent,

    /// <summary>Sign-in, identity, permissions, tokens.</summary>
    Auth,

    /// <summary>A schema change. Section 15 classifies these; the recap only has to surface them.</summary>
    Migration,

    /// <summary>Money maths: pricing, tax, invoicing, totals.</summary>
    MoneyMath,

    /// <summary>Talks to something outside the process.</summary>
    ExternalCall,

    /// <summary>Secrets, keys, encryption.</summary>
    Secrets,

    /// <summary>CI, deployment, containers, infrastructure as code.</summary>
    Infrastructure,

    /// <summary>Dependency manifests and lockfiles.</summary>
    Dependencies,

    /// <summary>Runtime configuration.</summary>
    Configuration,

    /// <summary>The file was deleted.</summary>
    Deletion,

    /// <summary>A test. Sinks (section 14).</summary>
    Test,

    /// <summary>Formatting only. Sinks (section 14).</summary>
    Formatting,

    /// <summary>Documentation. Sinks (section 14).</summary>
    Documentation,
}

/// <summary>Where a file lands once it is scored.</summary>
public enum RecapRiskBand
{
    /// <summary>Read this before anything else.</summary>
    Critical,

    High,

    Moderate,

    Low,

    /// <summary>Tests, formatting, documentation. Read last, or not at all.</summary>
    Minimal,
}

/// <summary>One scored file, with the reasons that put it where it is.</summary>
/// <param name="Change">The file.</param>
/// <param name="Score">The computed score. Higher is riskier; negative is below an ordinary file.</param>
/// <param name="Band">The band the score falls in.</param>
/// <param name="Factors">Every factor that applied, strongest first.</param>
/// <param name="Reasons">One short engineer-facing phrase per factor, in the same order.</param>
public sealed record RecapRankedFile(
    RecapFileChange Change,
    int Score,
    RecapRiskBand Band,
    IReadOnlyList<RecapRiskFactor> Factors,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The path, for convenience.</summary>
    public string Path => Change.Path;
}

/// <summary>
/// Section 14's risk ranking: <em>auth, migrations, money maths, external calls and
/// denylist-adjacent paths float to the top; tests and formatting sink</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Charter computes this, not the model.</strong> The ordering is the part of the recap an
/// engineer acts on first, and a model asked to sort a list will sometimes sort it alphabetically,
/// sometimes by the order it was given, and sometimes by how interesting it found each file. So the
/// order is decided here, deterministically, and the model is given the ranked list and asked only
/// to say what each file does. That also makes the ordering testable without a model in the loop.
/// </para>
/// <para>
/// Ties break on the size of the change and then on the order the caller supplied — never on the
/// name. Alphabetical ordering is the specific failure section 14 calls out: it puts
/// <c>src/Auth/TokenIssuer.cs</c> below <c>docs/README.md</c> for no reason anyone can defend.
/// </para>
/// </remarks>
public static class RecapFileRiskRanker
{
    private const int BaseScore = 10;

    private static readonly string[] AuthWords =
    [
        "auth", "authn", "authz", "login", "signin", "sign-in", "logout", "identity", "password",
        "permission", "authorize", "authorise", "authorization", "authorisation", "rbac", "acl",
        "oauth", "jwt", "principal", "claims", "role",
    ];

    private static readonly string[] SecretWords =
    [
        "secret", "credential", "keyring", "keyvault", "vault", "encrypt", "decrypt", "cipher",
        "protector", "signing",
    ];

    private static readonly string[] MoneyWords =
    [
        "billing", "payment", "invoice", "price", "pricing", "charge", "tax", "vat", "refund",
        "ledger", "money", "currency", "discount", "total", "subtotal", "quote", "budget",
        "subscription", "checkout", "payout", "fee", "cost",
    ];

    private static readonly string[] ExternalCallFileWords =
    [
        "client", "http", "webhook", "gateway", "sdk", "integration", "grpc", "smtp", "soap",
    ];

    private static readonly string[] ExternalCallSegments =
    [
        "api", "apis", "clients", "integrations", "webhooks", "providers", "adapters",
    ];

    private static readonly string[] InfrastructureSegments =
    [
        ".github", ".gitlab", "infra", "infrastructure", "terraform", "deploy", "deployments",
        "docker", "k8s", "kubernetes", "helm", "charts", "ansible", "ops", "pipelines",
    ];

    private static readonly string[] InfrastructureFiles =
    [
        "dockerfile", "docker-compose.yml", "docker-compose.yaml", "procfile", "makefile",
        "railway.json", "fly.toml", "nixpacks.toml",
    ];

    private static readonly string[] DependencyFiles =
    [
        "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "go.mod", "go.sum",
        "cargo.toml", "cargo.lock", "requirements.txt", "poetry.lock", "pipfile", "pipfile.lock",
        "gemfile", "gemfile.lock", "directory.packages.props", "paket.dependencies",
    ];

    private static readonly string[] DependencyExtensions = [".csproj", ".fsproj", ".vbproj", ".sln"];

    private static readonly string[] TestSegments =
    [
        "test", "tests", "__tests__", "spec", "specs", "e2e", "fixtures", "testdata",
    ];

    private static readonly string[] FormattingFiles =
    [
        ".editorconfig", ".prettierrc", ".prettierrc.json", ".prettierignore", ".eslintrc",
        ".eslintrc.json", ".eslintrc.cjs", ".stylelintrc", ".stylelintrc.json", ".gitattributes",
    ];

    private static readonly string[] DocumentationExtensions = [".md", ".mdx", ".rst", ".txt", ".adoc"];

    private static readonly string[] ConfigurationExtensions = [".yml", ".yaml", ".toml", ".ini", ".env"];

    /// <summary>
    /// Ranks a change. Deterministic, pure, and independent of the model.
    /// </summary>
    /// <param name="files">The files the session touched.</param>
    /// <param name="denyPatterns">
    /// The repository's <c>scopes.deny</c> globs (section 8). Anything matching one, or sitting
    /// beside something that does, floats to the top of the list.
    /// </param>
    public static IReadOnlyList<RecapRankedFile> Rank(
        IEnumerable<RecapFileChange> files,
        IEnumerable<string>? denyPatterns = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        var deny = (denyPatterns ?? [])
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => pattern.Trim())
            .ToList();

        var ranked = files
            .Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
            .Select((file, index) => (Ranked: Score(file, deny), Index: index))
            .ToList();

        return
        [
            .. ranked
                .OrderByDescending(static entry => entry.Ranked.Score)
                .ThenByDescending(static entry => entry.Ranked.Change.LinesChanged)
                .ThenBy(static entry => entry.Index)
                .Select(static entry => entry.Ranked),
        ];
    }

    /// <summary>Scores one file.</summary>
    public static RecapRankedFile Score(RecapFileChange file, IReadOnlyList<string>? denyPatterns = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        var path = GlobPattern.Normalise(file.Path).ToLowerInvariant();
        var fileName = FileNameOf(path);
        var stem = StemOf(fileName);
        var extension = ExtensionOf(fileName);
        var segments = DirectorySegments(path);
        var weighted = new List<(RecapRiskFactor Factor, int Weight, string Reason)>();

        var deny = denyPatterns ?? [];
        if (deny.Any(pattern => GlobPattern.IsMatch(pattern, path)))
        {
            weighted.Add((
                RecapRiskFactor.Denylisted,
                60,
                "matches a path this repository denies the agent"));
        }
        else if (IsDenylistAdjacent(path, deny))
        {
            weighted.Add((
                RecapRiskFactor.DenylistAdjacent,
                30,
                "sits beside an area the agent is not allowed into"));
        }

        if (IsMigration(path, segments, extension, stem))
        {
            weighted.Add((RecapRiskFactor.Migration, 50, "schema change"));
        }

        if (MatchesAny(path, AuthWords))
        {
            weighted.Add((RecapRiskFactor.Auth, 45, "sign-in, identity or permissions"));
        }

        if (MatchesAny(path, SecretWords))
        {
            weighted.Add((RecapRiskFactor.Secrets, 45, "secrets or cryptography"));
        }

        if (MatchesAny(path, MoneyWords))
        {
            weighted.Add((RecapRiskFactor.MoneyMath, 40, "money maths"));
        }

        if (MatchesAny(fileName, ExternalCallFileWords)
            || segments.Any(segment => ExternalCallSegments.Contains(segment, StringComparer.Ordinal)))
        {
            weighted.Add((RecapRiskFactor.ExternalCall, 30, "calls something outside the process"));
        }

        if (segments.Any(segment => InfrastructureSegments.Contains(segment, StringComparer.Ordinal))
            || InfrastructureFiles.Contains(fileName, StringComparer.Ordinal))
        {
            weighted.Add((RecapRiskFactor.Infrastructure, 25, "build, deployment or infrastructure"));
        }

        var isDependencyManifest = DependencyFiles.Contains(fileName, StringComparer.Ordinal)
            || DependencyExtensions.Contains(extension, StringComparer.Ordinal);
        if (isDependencyManifest)
        {
            weighted.Add((RecapRiskFactor.Dependencies, 25, "dependency manifest"));
        }

        if (fileName.StartsWith("appsettings", StringComparison.Ordinal)
            || fileName.StartsWith(".env", StringComparison.Ordinal)
            || segments.Contains("config", StringComparer.Ordinal)
            || segments.Contains("configuration", StringComparer.Ordinal)
            || ConfigurationExtensions.Contains(extension, StringComparer.Ordinal))
        {
            weighted.Add((RecapRiskFactor.Configuration, 20, "runtime configuration"));
        }

        if (file.Kind == RecapFileChangeKind.Deleted)
        {
            weighted.Add((RecapRiskFactor.Deletion, 10, "deleted"));
        }

        if (IsTest(segments, stem, fileName))
        {
            weighted.Add((RecapRiskFactor.Test, -35, "a test"));
        }

        if (file.FormattingOnly || FormattingFiles.Contains(fileName, StringComparer.Ordinal))
        {
            weighted.Add((RecapRiskFactor.Formatting, -30, "formatting only"));
        }

        if (!isDependencyManifest
            && (DocumentationExtensions.Contains(extension, StringComparer.Ordinal)
                || segments.Contains("docs", StringComparer.Ordinal)))
        {
            weighted.Add((RecapRiskFactor.Documentation, -25, "documentation"));
        }

        var ordered = weighted
            .OrderByDescending(static entry => entry.Weight)
            .ToList();

        var score = BaseScore + ordered.Sum(static entry => entry.Weight);

        return new RecapRankedFile(
            file,
            score,
            BandFor(score),
            [.. ordered.Select(static entry => entry.Factor)],
            [.. ordered.Select(static entry => entry.Reason)]);
    }

    /// <summary>The band a score falls in.</summary>
    public static RecapRiskBand BandFor(int score) => score switch
    {
        >= 60 => RecapRiskBand.Critical,
        >= 35 => RecapRiskBand.High,
        >= 15 => RecapRiskBand.Moderate,
        >= 0 => RecapRiskBand.Low,
        _ => RecapRiskBand.Minimal,
    };

    /// <summary>The heading a band renders under.</summary>
    public static string DescribeBand(RecapRiskBand band) => band switch
    {
        RecapRiskBand.Critical => "highest risk",
        RecapRiskBand.High => "high risk",
        RecapRiskBand.Moderate => "moderate risk",
        RecapRiskBand.Low => "ordinary",
        _ => "lowest risk",
    };

    private static bool IsDenylistAdjacent(string path, IReadOnlyList<string> denyPatterns)
    {
        var directory = DirectoryOf(path);
        var parent = ParentOf(directory);
        if (parent.Length == 0)
        {
            return false;
        }

        foreach (var pattern in denyPatterns)
        {
            var deniedDirectory = LiteralDirectory(pattern);
            if (deniedDirectory.Length == 0)
            {
                continue;
            }

            // A sibling of a denied directory. `src/Auth/**` denied, `src/AuthShared/Token.cs`
            // touched: not denied, and not something to read tenth either.
            if (string.Equals(ParentOf(deniedDirectory), parent, StringComparison.Ordinal)
                && !string.Equals(deniedDirectory, directory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMigration(string path, IReadOnlyList<string> segments, string extension, string stem)
        => segments.Contains("migrations", StringComparer.Ordinal)
        || segments.Contains("migration", StringComparer.Ordinal)
        || string.Equals(extension, ".sql", StringComparison.Ordinal)
        || stem.Contains("migration", StringComparison.Ordinal)
        || stem.Contains("schema", StringComparison.Ordinal)
        || path.Contains("modelsnapshot", StringComparison.Ordinal);

    private static bool IsTest(IReadOnlyList<string> segments, string stem, string fileName)
        => segments.Any(segment => TestSegments.Contains(segment, StringComparer.Ordinal))
        || stem.EndsWith("tests", StringComparison.Ordinal)
        || stem is "test" or "spec"
        || stem.EndsWith(".test", StringComparison.Ordinal)
        || stem.EndsWith("-test", StringComparison.Ordinal)
        || stem.EndsWith("_test", StringComparison.Ordinal)
        || stem.EndsWith(".spec", StringComparison.Ordinal)
        || fileName.Contains(".test.", StringComparison.Ordinal)
        || fileName.Contains(".spec.", StringComparison.Ordinal);

    private static bool MatchesAny(string haystack, string[] needles)
        => needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string StemOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    private static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? string.Empty : fileName[dot..];
    }

    private static IReadOnlyList<string> DirectorySegments(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? [] : [.. path[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..(slash + 1)];
    }

    private static string ParentOf(string directory)
    {
        if (directory.Length == 0)
        {
            return string.Empty;
        }

        var trimmed = directory.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..(slash + 1)];
    }

    private static string LiteralDirectory(string pattern)
    {
        var normalised = GlobPattern.Normalise(pattern).ToLowerInvariant();
        var wildcard = normalised.IndexOfAny(['*', '?']);
        var literal = wildcard < 0 ? normalised : normalised[..wildcard];
        var slash = literal.LastIndexOf('/');
        return slash < 0 ? string.Empty : literal[..(slash + 1)];
    }
}
