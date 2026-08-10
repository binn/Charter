using System.Text.Json;
using Charter.Auth.Authorization;
using Charter.GitHub;
using Charter.Onboarding;
using Charter.Refinement;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Parsing the committed <c>.charter/</c> folder (section 8).
/// </summary>
/// <remarks>
/// The rule under test throughout is section 8's extensibility contract: <c>version: 1</c> at the
/// top, and <strong>unknown keys warn, never fail</strong>, so an old Charter keeps working against a
/// repository written for a newer one.
/// </remarks>
public class OnboardingCharterFolderTests
{
    private const string Sha = "abc1234";

    [Fact]
    public void AFullFolderIsRead()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = """
                                          version: 1
                                          base_branch: main
                                          runner_image: ghcr.io/binn/charter-runner-dotnet:1
                                          seed: "dotnet run --project tools/Seed"
                                          name: Quote tool
                                          scopes:
                                            allow:
                                              - "src/Features/**"
                                            deny:
                                              - "**/Migrations/**"
                                          checks:
                                            - name: build
                                              run: "dotnet build"
                                          limits:
                                            max_session_usd: 5.00
                                            max_files_changed: 40
                                          """,
                [".charter/conventions.md"] = "# Conventions\n\nUse records.",
                [".charter/primer.md"] = "# How this app is put together",
                [".charter/glossary.yml"] = "version: 1\nBOQ: \"Bill of Quantities.\"\nderate: \"Reducing a rated output.\"",
                [".charter/templates/copy-change.yml"] = "version: 1\nname: Change some text\nprompt: I want to change…",
                [".charter/checks/lint.yml"] = "version: 1\nname: lint\nrun: \"dotnet format --verify-no-changes\"",
                [".charter/policies/migrations.yml"] = "version: 1\ndestructive:\n  - drop_view",
            },
            Sha);

        Assert.True(folder.Exists);
        Assert.Equal(Sha, folder.CommitSha);
        Assert.Equal("main", folder.Config.BaseBranch);
        Assert.Equal("ghcr.io/binn/charter-runner-dotnet:1", folder.Config.RunnerImage);
        Assert.Equal("dotnet run --project tools/Seed", folder.Config.Seed);
        Assert.Equal("Quote tool", folder.Config.ProjectName);
        Assert.Equal(["src/Features/**"], folder.Config.Allow);
        Assert.Equal(["**/Migrations/**"], folder.Config.Deny);
        Assert.Equal(5.00m, folder.Config.Limits.MaxSessionUsd);
        Assert.Equal(40, folder.Config.Limits.MaxFilesChanged);

        Assert.Contains("Use records.", folder.ConventionsMarkdown, StringComparison.Ordinal);
        Assert.Contains("How this app", folder.PrimerMarkdown, StringComparison.Ordinal);
        Assert.Equal(2, folder.Glossary.Terms.Count);

        var template = Assert.Single(folder.Templates);
        Assert.Equal("copy-change", template.Id);
        Assert.Equal("Change some text", template.Name);

        // Both the inline check and the one in checks/ survive, and neither shadows the other.
        Assert.Equal(2, folder.Checks.Count);
        Assert.Contains(folder.Checks, check => check.Name == "build");
        Assert.Contains(folder.Checks, check => check.Name == "lint");

        Assert.Equal(MigrationClass.Destructive, folder.Migrations.Classify("drop_view"));
        Assert.Empty(folder.Warnings);
    }

    [Fact]
    public void UnknownKeysWarnAndDoNotFail()
    {
        // The whole point of section 8's extensibility rules: a repository written for a newer
        // Charter must still work on this one, minus the keys this one has never heard of.
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = """
                                          version: 1
                                          base_branch: main
                                          quantum_mode: entangled
                                          scopes:
                                            allow:
                                              - "src/Features/**"
                                            maybe:
                                              - "src/Perhaps/**"
                                          limits:
                                            max_session_usd: 5.00
                                            max_vibes: 11
                                          """,
            },
            Sha);

        Assert.True(folder.Exists);

        // Everything it did understand still came through.
        Assert.Equal("main", folder.Config.BaseBranch);
        Assert.Equal(["src/Features/**"], folder.Config.Allow);
        Assert.Equal(5.00m, folder.Config.Limits.MaxSessionUsd);

        Assert.Contains(folder.Warnings, warning => warning.Contains("quantum_mode", StringComparison.Ordinal));
        Assert.Contains(folder.Warnings, warning => warning.Contains("scopes.maybe", StringComparison.Ordinal));
        Assert.Contains(folder.Warnings, warning => warning.Contains("limits.max_vibes", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewerFileVersionWarnsRatherThanRefusing()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = "version: 9\nbase_branch: trunk\n",
            },
            Sha);

        Assert.Equal(9, folder.Config.Version);
        Assert.Equal("trunk", folder.Config.BaseBranch);
        Assert.Contains(folder.Warnings, warning => warning.Contains("newer than this Charter", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingVersionKeyWarns()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = "base_branch: main\n",
            },
            Sha);

        Assert.Equal(1, folder.Config.Version);
        Assert.False(folder.Config.DeclaredVersion);
        Assert.Contains(folder.Warnings, warning => warning.Contains("version: 1", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFolderIsAnOrdinaryStateWithAWarning()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = "# Widgets" },
            Sha);

        Assert.False(folder.Exists);
        Assert.Single(folder.Warnings);
        Assert.Contains("no .charter/ folder", folder.Warnings[0], StringComparison.Ordinal);

        // Deny by default falls straight out of it: an unconfigured repository refuses everything.
        var context = folder.ToRefinementContext(repositoryFullName: "acme/widgets");

        Assert.Empty(context.Scope.Allow);
        Assert.False(context.Scope.Evaluate(["src/Features/Thing.cs"]).IsAllowed);
    }

    [Fact]
    public void UnparseableYamlFailsClosedRatherThanOpen()
    {
        // An unreadable guardrail file must never read as "no guardrails".
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = "scopes:\n  allow:\n   - \"a\"\n\t- broken tab",
            },
            Sha);

        Assert.True(folder.Exists);
        Assert.Empty(folder.Config.Allow);
        Assert.NotEmpty(folder.Warnings);
    }

    [Fact]
    public void TheCacheDirectoryIsNeverRead()
    {
        // Section 8 gitignores .charter/cache/ in the target repository; Charter caches its own side.
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = "version: 1\n",
                [".charter/cache/recon.json"] = """{"stack":"whatever"}""",
                [".charter/cache/templates/ignored.yml"] = "name: Not a template",
            },
            Sha);

        Assert.Empty(folder.Templates);
        Assert.DoesNotContain(folder.Warnings, warning => warning.Contains("cache", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFolderFillsTheRefinementContext()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = """
                                          version: 1
                                          name: Quote tool
                                          scopes:
                                            allow:
                                              - "src/Features/**"
                                            deny:
                                              - "src/Auth/**"
                                          """,
                [".charter/glossary.yml"] = "version: 1\nBOQ: \"Bill of Quantities.\"",
                [".charter/primer.md"] = "The app is a quoting tool.",
                [".charter/conventions.md"] = "Prefer records.",
            },
            Sha);

        var standards = StandardsDocument.Parse("version: 2\nservices:\n  ai:\n    provider: openrouter\n");
        var context = folder.ToRefinementContext(standards, "acme/quote-tool");

        Assert.Equal("Quote tool", context.ProjectName);
        Assert.Equal("The app is a quoting tool.", context.PrimerMarkdown);
        Assert.Equal("Prefer records.", context.ConventionsMarkdown);
        Assert.Contains("BOQ", context.Glossary.Terms.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, context.Standards.Version);

        Assert.True(context.Scope.Evaluate(["src/Features/Quotes.cs"]).IsAllowed);
        Assert.False(context.Scope.Evaluate(["src/Auth/Login.cs"]).IsAllowed);
    }

    [Fact]
    public void TheProjectNameFallsBackToTheRepositoryNameAndNeverToTheSlug()
    {
        // Section 7.1: a requester never sees owner/repo.
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal) { [".charter/config.yml"] = "version: 1\n" },
            Sha);

        Assert.Equal("Quote tool", folder.ToRefinementContext(repositoryFullName: "acme/quote-tool").ProjectName);
    }

    [Fact]
    public void TheSnapshotIsReadableByTheAuthoriserAndTheProjectList()
    {
        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = """
                                          version: 1
                                          name: Quote tool
                                          scopes:
                                            allow:
                                              - "src/Features/**"
                                          limits:
                                            max_session_usd: 5.00
                                          """,
                [".charter/glossary.yml"] = "version: 1\nBOQ: \"Bill of Quantities.\"",
            },
            Sha);

        var json = folder.ToSnapshotJson();

        // The two existing readers of charter_config_snapshot must both make sense of what this
        // writes, or the column means one thing to the writer and another to the reader.
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction(json);

        Assert.Equal(["src/Features/**"], restriction.PathAllowList);
        Assert.Equal(5.00m, restriction.MaxCostUsdCeiling);
        Assert.False(restriction.DisallowAutoDispatch);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Quote tool", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            "Bill of Quantities.",
            document.RootElement.GetProperty("glossary").GetProperty("BOQ").GetString());
    }

    [Fact]
    public void AnUnknownMigrationOperationIsAmbiguousRatherThanAdditive()
    {
        var policy = MigrationPolicyDocument.Default;

        Assert.Equal(MigrationClass.Additive, policy.Classify("CreateTable"));
        Assert.Equal(MigrationClass.Ambiguous, policy.Classify("RenameColumn"));
        Assert.Equal(MigrationClass.Destructive, policy.Classify("DropColumn"));

        // The safe default: an operation this version has never seen is exactly where a human
        // should look.
        Assert.Equal(MigrationClass.Ambiguous, policy.Classify("SomethingEfInventedLastWeek"));
    }
}

/// <summary>The loader, its cache, and the fact that neither touches the network more than once.</summary>
public class OnboardingCharterFolderLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ABranchIsResolvedToACommitBeforeAnythingIsCached()
    {
        var github = new FakeRepositoryClient
        {
            BranchHeads = { ["main"] = "commit1" },
            Files =
            {
                [".charter/config.yml"] = "version: 1\nbase_branch: main\n",
            },
        };

        var cache = new CharterFolderCache();
        var loader = Loader(github, cache);

        var first = await loader.LoadAsync(
            GitHubTestFixtures.Repository,
            "main",
            TestContext.Current.CancellationToken);

        Assert.Equal("commit1", first.CommitSha);

        // Caching against "main" would serve yesterday's guardrails after somebody pushes.
        Assert.NotNull(cache.Get("acme/widgets", "commit1"));
        Assert.Null(cache.Get("acme/widgets", "main"));
    }

    [Fact]
    public async Task TheSecondLoadOfACommitCostsNothing()
    {
        var github = new FakeRepositoryClient
        {
            Files = { [".charter/config.yml"] = "version: 1\n" },
        };

        var loader = Loader(github, new CharterFolderCache());

        await loader.LoadAsync(GitHubTestFixtures.Repository, "commit1", TestContext.Current.CancellationToken);
        await loader.LoadAsync(GitHubTestFixtures.Repository, "commit1", TestContext.Current.CancellationToken);

        Assert.Equal(1, github.TreeListings);
    }

    [Fact]
    public async Task ADifferentCommitIsADifferentEntry()
    {
        var github = new FakeRepositoryClient
        {
            Files = { [".charter/config.yml"] = "version: 1\n" },
        };

        var loader = Loader(github, new CharterFolderCache());

        await loader.LoadAsync(GitHubTestFixtures.Repository, "commit1", TestContext.Current.CancellationToken);
        await loader.LoadAsync(GitHubTestFixtures.Repository, "commit2", TestContext.Current.CancellationToken);

        Assert.Equal(2, github.TreeListings);
    }

    [Fact]
    public async Task ARepositoryWithNoCharterFolderLoadsAsMissing()
    {
        var loader = Loader(new FakeRepositoryClient(), new CharterFolderCache());

        var folder = await loader.LoadAsync(
            GitHubTestFixtures.Repository,
            "commit1",
            TestContext.Current.CancellationToken);

        Assert.False(folder.Exists);
        Assert.NotEmpty(folder.Warnings);
    }

    [Fact]
    public async Task EvictingForgetsOneRepositoryOnly()
    {
        var github = new FakeRepositoryClient { Files = { [".charter/config.yml"] = "version: 1\n" } };
        var cache = new CharterFolderCache();
        var loader = Loader(github, cache);

        await loader.LoadAsync(GitHubTestFixtures.Repository, "commit1", TestContext.Current.CancellationToken);
        await loader.LoadAsync(
            GitHubRepository.Parse("acme/other", 4242),
            "commit1",
            TestContext.Current.CancellationToken);

        cache.Evict("acme/widgets");

        Assert.Null(cache.Get("acme/widgets", "commit1"));
        Assert.NotNull(cache.Get("acme/other", "commit1"));
    }

    [Fact]
    public void TheCacheEvictsOldestFirstWhenFull()
    {
        var cache = new CharterFolderCache(capacity: 2);

        cache.Set("acme/widgets", "c1", CharterFolder.Missing("c1"));
        cache.Set("acme/widgets", "c2", CharterFolder.Missing("c2"));
        cache.Set("acme/widgets", "c3", CharterFolder.Missing("c3"));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("acme/widgets", "c1"));
        Assert.NotNull(cache.Get("acme/widgets", "c3"));
    }

    private static CharterFolderLoader Loader(FakeRepositoryClient github, CharterFolderCache cache)
        => new(github, cache, GitHubTestFixtures.Options(), NullLogger<CharterFolderLoader>.Instance);
}

/// <summary>A repository client backed by a dictionary. No HTTP, no GitHub.</summary>
internal sealed class FakeRepositoryClient : IGitHubRepositoryClient
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);

    public List<GitHubFileEdit> Committed { get; } = [];

    public List<string> BranchesCreated { get; } = [];

    public List<(string Head, string Base, string Title, string Body)> PullRequests { get; } = [];

    public int TreeListings { get; private set; }

    public Dictionary<int, List<string>> Labels { get; } = [];

    public List<(int Number, string Body)> Comments { get; } = [];

    public Dictionary<(string Base, string Head), GitHubComparison> Comparisons { get; } = [];

    public Dictionary<string, GitHubBranchProtection> Protection { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Webhooks { get; } = new(StringComparer.Ordinal);

    public List<string> Created { get; } = [];

    public List<(string Repository, string Owner)> Transferred { get; } = [];

    /// <summary>When set, every call throws it — the "GitHub is unreachable" path.</summary>
    public GitHubApiException? Failure { get; set; }

    public Task<string?> GetBranchHeadShaAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        Throw();

        return Task.FromResult(BranchHeads.TryGetValue(branch, out var sha) ? sha : null);
    }

    public Task<GitHubFile?> GetFileAsync(
        GitHubRepository repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        Throw();

        return Task.FromResult(
            Files.TryGetValue(path, out var text) ? new GitHubFile(path, "sha-" + path, text) : null);
    }

    public Task<IReadOnlyList<GitHubTreeEntry>> ListTreeAsync(
        GitHubRepository repository,
        string reference,
        CancellationToken cancellationToken = default)
    {
        Throw();
        TreeListings++;

        return Task.FromResult<IReadOnlyList<GitHubTreeEntry>>(
            [.. Files.Keys.Select(path => new GitHubTreeEntry(path, "blob", "sha-" + path, Files[path].Length))]);
    }

    public Task<string> GetBlobTextAsync(
        GitHubRepository repository,
        string sha,
        CancellationToken cancellationToken = default)
    {
        Throw();

        var path = sha.StartsWith("sha-", StringComparison.Ordinal) ? sha[4..] : sha;

        return Task.FromResult(Files.TryGetValue(path, out var text) ? text : string.Empty);
    }

    public Task CreateBranchAsync(
        GitHubRepository repository,
        string branch,
        string fromSha,
        CancellationToken cancellationToken = default)
    {
        Throw();
        BranchesCreated.Add(branch);
        BranchHeads[branch] = fromSha;

        return Task.CompletedTask;
    }

    public Task<GitHubCommitResult> CommitFilesAsync(
        GitHubRepository repository,
        string branch,
        string message,
        IReadOnlyList<GitHubFileEdit> files,
        CancellationToken cancellationToken = default)
    {
        Throw();
        Committed.AddRange(files);

        foreach (var file in files)
        {
            Files[file.Path] = file.Text;
        }

        BranchHeads[branch] = "commit-" + Committed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Task.FromResult(new GitHubCommitResult(BranchHeads[branch], branch));
    }

    public Task<GitHubPullRequestResult> OpenPullRequestAsync(
        GitHubRepository repository,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        Throw();
        PullRequests.Add((headBranch, baseBranch, title, body));

        return Task.FromResult(new GitHubPullRequestResult(
            PullRequests.Count,
            $"https://github.com/{repository.FullName}/pull/{PullRequests.Count}",
            "headsha",
            headBranch));
    }

    public Task<GitHubCommitResult> UpdateBranchAsync(
        GitHubRepository repository,
        string branch,
        string sha,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        Throw();
        BranchHeads[branch] = sha;

        return Task.FromResult(new GitHubCommitResult(sha, branch));
    }

    public Task<GitHubPullRequestDetail?> GetPullRequestAsync(
        GitHubRepository repository,
        int number,
        CancellationToken cancellationToken = default)
    {
        Throw();

        if (number < 1 || number > PullRequests.Count)
        {
            return Task.FromResult<GitHubPullRequestDetail?>(null);
        }

        var (head, target, _, _) = PullRequests[number - 1];

        return Task.FromResult<GitHubPullRequestDetail?>(new GitHubPullRequestDetail(
            number,
            $"https://github.com/{repository.FullName}/pull/{number}",
            "open",
            false,
            false,
            BranchHeads.TryGetValue(head, out var sha) ? sha : "headsha",
            head,
            target,
            Labels.TryGetValue(number, out var labels) ? labels : []));
    }

    public Task CommentOnPullRequestAsync(
        GitHubRepository repository,
        int number,
        string body,
        CancellationToken cancellationToken = default)
    {
        Throw();
        Comments.Add((number, body));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> AddLabelsAsync(
        GitHubRepository repository,
        int number,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        Throw();

        var applied = Labels.TryGetValue(number, out var existing) ? [.. existing] : new List<string>();
        applied.AddRange(labels.Where(label => !applied.Contains(label, StringComparer.Ordinal)));
        Labels[number] = applied;

        return Task.FromResult<IReadOnlyList<string>>(applied);
    }

    public Task<GitHubComparison> CompareAsync(
        GitHubRepository repository,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default)
    {
        Throw();

        return Task.FromResult(Comparisons.TryGetValue((baseRevision, headRevision), out var comparison)
            ? comparison
            : new GitHubComparison(0, 0, []));
    }

    public Task<GitHubBranchProtection> GetBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        Throw();

        return Task.FromResult(Protection.TryGetValue(branch, out var protection)
            ? protection
            : new GitHubBranchProtection(false, Detail: $"no branch protection rule covers '{branch}'"));
    }

    public Task ApplyBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        int requiredApprovals,
        bool requireCodeOwnerReview,
        bool dismissStaleReviews,
        bool enforceForAdministrators,
        CancellationToken cancellationToken = default)
    {
        Throw();

        Protection[branch] = new GitHubBranchProtection(
            true,
            requiredApprovals,
            requireCodeOwnerReview,
            dismissStaleReviews,
            enforceForAdministrators,
            $"'{branch}' requires review before merge");

        return Task.CompletedTask;
    }

    public Task<GitHubWebhookHook> RegisterWebhookAsync(
        GitHubRepository repository,
        Uri callbackUrl,
        string secret,
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        Throw();

        var created = Webhooks.Add(callbackUrl.ToString());

        return Task.FromResult(new GitHubWebhookHook(Webhooks.Count, callbackUrl.ToString(), created));
    }

    public Task<GitHubRepositorySummary> CreateRepositoryAsync(
        long installationId,
        string owner,
        string name,
        bool isPrivate,
        string? description,
        CancellationToken cancellationToken = default)
    {
        Throw();
        Created.Add($"{owner}/{name}");

        return Task.FromResult(new GitHubRepositorySummary($"{owner}/{name}", "main", isPrivate));
    }

    public Task<GitHubRepositorySummary> TransferRepositoryAsync(
        GitHubRepository repository,
        string newOwner,
        CancellationToken cancellationToken = default)
    {
        Throw();
        Transferred.Add((repository.FullName, newOwner));

        return Task.FromResult(new GitHubRepositorySummary(
            $"{newOwner}/{repository.Name}",
            "main",
            true));
    }

    private void Throw()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
