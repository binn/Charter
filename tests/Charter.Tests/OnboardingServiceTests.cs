using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.GitHub;
using Charter.Onboarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The onboarding flow and the query path, against a real Postgres.
/// </summary>
/// <remarks>
/// Section 9's central claim — a repository is invisible to requesters until its smoke test passes —
/// is a property of a SQL query, so asserting it against an in-memory list would prove nothing. These
/// run only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway database and skip otherwise,
/// following <c>DataJobQueueTests</c>. Every test namespaces its own organisation, so a shared
/// database stays usable.
/// </remarks>
public class OnboardingServiceTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task AConnectedRepositoryIsRequestableByNobody()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.Service.ConnectAsync(
            world.OrgId,
            4242,
            world.RepoName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RepoStatus.Pending, repo.Status);
        Assert.False(repo.IsRequesterVisible);

        // Section 7.3: the absence of a scope row is the refusal, so there is nothing to revoke.
        Assert.Empty(await world.Db.RepoScopes
            .Where(scope => scope.RepoId == repo.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Empty(await world.RequestableAsync(MemberRole.Requester));
    }

    [Fact]
    public async Task ConnectingTheSameRepositoryTwiceIsIdempotent()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var first = await world.Service.ConnectAsync(
            world.OrgId, 4242, world.RepoName, cancellationToken: TestContext.Current.CancellationToken);

        var second = await world.Service.ConnectAsync(
            world.OrgId, 4242, world.RepoName, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ARepoIsInvisibleToRequestersUntilTheSmokeTestPasses()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();

        // The most permissive grant possible, deliberately: this test is about readiness, not scope.
        world.Db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester));
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var status in (RepoStatus[])
                 [RepoStatus.Pending, RepoStatus.Recon, RepoStatus.Configuring, RepoStatus.SmokeTest])
        {
            repo.TransitionTo(status);
            await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Not merely refused — absent. A requester cannot learn that the repository exists.
            Assert.Empty(await world.RequestableAsync(MemberRole.Requester));
            Assert.False(await world.CanFileAsync(repo.Id, MemberRole.Requester));

            // The single-repository authoriser agrees, because they are two expressions of one rule.
            Assert.False(RepoAccessPolicy.CanFileRequest(
                world.Member(MemberRole.Requester),
                world.Snapshot(repo, canRequest: true)).IsAllowed);
        }

        repo.TransitionTo(RepoStatus.Ready);
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await world.RequestableAsync(MemberRole.Requester));
        Assert.True(await world.CanFileAsync(repo.Id, MemberRole.Requester));
    }

    [Fact]
    public async Task AReadyRepositoryWithoutAGrantIsStillInvisible()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();
        repo.TransitionTo(RepoStatus.Ready);
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Readiness is necessary, not sufficient. Both guardrails are separately load-bearing.
        Assert.Empty(await world.RequestableAsync(MemberRole.Requester));
    }

    [Fact]
    public async Task AWithholdingRowBeatsAGrantingOne()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();
        repo.TransitionTo(RepoStatus.Ready);

        world.Db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester));
        world.Db.RepoScopes.Add(RepoScope.ForMember(repo.Id, world.MemberId, canRequest: false));
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await world.RequestableAsync(MemberRole.Requester));
    }

    [Fact]
    public async Task ADisabledRepositoryDisappearsAgain()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();
        repo.TransitionTo(RepoStatus.Ready);
        world.Db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester));
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await world.RequestableAsync(MemberRole.Requester));

        await world.Service.SetEnabledAsync(
            repo.Id,
            enabled: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await world.RequestableAsync(MemberRole.Requester));
    }

    [Fact]
    public async Task ReconIsDispatchedThroughTheExecutionPlaneSeam()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();

        var outcome = await world.Service.StartReconAsync(
            repo.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(RepoStatus.Recon, outcome.Status);

        var dispatched = Assert.Single(world.Dispatcher.Recon);

        Assert.Equal(repo.Id, dispatched.RepoId);
        Assert.Equal(world.RepoName, dispatched.RepoFullName);
        Assert.Equal(4242, dispatched.InstallationId);

        // Section 9, step 2: the recon session is read-only, and nothing downstream has to infer it.
        Assert.True(dispatched.ReadOnly);
    }

    [Fact]
    public async Task ReconWritesTheScopeConfigAsAPullRequestAndNeverAsACommit()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();
        await world.Service.StartReconAsync(repo.Id, cancellationToken: TestContext.Current.CancellationToken);

        world.Github.BranchHeads["main"] = "basesha";

        var outcome = await world.Service.CompleteReconAsync(
            repo.Id,
            new ReconReport
            {
                DetectedStack = ["dotnet:10"],
                ProposedAllow = ["src/Features/**", "src/Migrations/**"],
                Checks = [new CharterCheck("build", "dotnet build")],
                ExistingGuidance = new ExistingAgentGuidance("# CLAUDE.md\n\n- Never commit to main.", null),
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(RepoStatus.Configuring, outcome.Status);

        // Section 8's whole point: changing a guardrail requires review, so the file arrives on a
        // branch behind a pull request and never on the base branch.
        var pullRequest = Assert.Single(world.Github.PullRequests);
        Assert.Equal(OnboardingService.ConfigBranch, pullRequest.Head);
        Assert.Equal("main", pullRequest.Base);
        Assert.Equal([OnboardingService.ConfigBranch], world.Github.BranchesCreated);

        var written = world.Github.Committed.Select(file => file.Path).ToList();
        Assert.Contains(".charter/config.yml", written, StringComparer.Ordinal);
        Assert.Contains(".charter/conventions.md", written, StringComparer.Ordinal);

        // Section 9: import and extend, never overwrite. The repository's own files are untouched.
        Assert.DoesNotContain("CLAUDE.md", written, StringComparer.Ordinal);
        Assert.DoesNotContain("AGENTS.md", written, StringComparer.Ordinal);

        var config = world.Github.Committed.First(file => file.Path == ".charter/config.yml").Text;
        Assert.Contains("src/Features/**", config, StringComparison.Ordinal);
        Assert.Contains("**/Migrations/**", config, StringComparison.Ordinal);

        // The migrations path recon suggested was refused, and the engineer is told why.
        Assert.Contains(outcome.Warnings, warning => warning.Contains("src/Migrations/**", StringComparison.Ordinal));
        Assert.DoesNotContain("allow:\n    - src/Migrations/**", config, StringComparison.Ordinal);

        // No seed was detected, and that is a warning rather than a blocker.
        Assert.Contains(outcome.Warnings, warning => warning.Contains("dev-seed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmingScopeQueuesTheSmokeTest()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ReachConfiguringAsync();

        var outcome = await world.Service.ConfirmScopeAsync(
            repo.Id,
            allow: ["src/Features/**", "infra/**"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(RepoStatus.SmokeTest, outcome.Status);
        Assert.Single(world.Dispatcher.SmokeTest);
        Assert.False(world.Dispatcher.SmokeTest[0].ReadOnly);

        // Even a hand-supplied allow list is filtered: a UI cannot widen past the deny-by-default
        // floor any more than recon can.
        var config = world.Github.Committed.Last(file => file.Path == ".charter/config.yml").Text;
        Assert.Contains("src/Features/**", config, StringComparison.Ordinal);
        Assert.DoesNotContain("    - infra/**", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassingSmokeTestMakesTheRepositoryRequestable()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ReachSmokeTestAsync();

        var outcome = await world.Service.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing with { PullRequestNumber = 17 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(RepoStatus.Ready, outcome.Status);
        Assert.Empty(outcome.Warnings);

        await world.Db.Entry(repo).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(repo.IsRequesterVisible);
    }

    [Fact]
    public async Task AnEmptyPreviewWarnsAndStillReachesReady()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ReachSmokeTestAsync();

        var outcome = await world.Service.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing with { PreviewHasData = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RepoStatus.Ready, outcome.Status);
        Assert.Contains(
            outcome.Warnings,
            warning => warning.Contains("appears to have no data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingSmokeTestLeavesTheRepositoryInvisible()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ReachSmokeTestAsync();

        var outcome = await world.Service.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing with { ChecksPassed = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RepoStatus.SmokeTest, outcome.Status);

        await world.Db.Entry(repo).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(repo.IsRequesterVisible);
    }

    [Fact]
    public async Task TheSnapshotIsReadBackOffTheBaseBranchRatherThanTrusted()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ReachSmokeTestAsync();

        // What is committed on the base branch is the truth, whatever Charter proposed earlier.
        world.Folders.Folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".charter/config.yml"] = "version: 1\nscopes:\n  allow:\n    - \"src/Only/**\"\n",
            },
            "mergedsha");

        await world.Service.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing,
            cancellationToken: TestContext.Current.CancellationToken);

        await world.Db.Entry(repo).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(repo.CharterConfigSnapshot);
        Assert.Contains("src/Only/**", repo.CharterConfigSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableGitHubDoesNotStrandOnboarding()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectAsync();
        await world.Service.StartReconAsync(repo.Id, cancellationToken: TestContext.Current.CancellationToken);

        world.Github.Failure = new GitHubApiException("GitHub is having a day.");

        var outcome = await world.Service.CompleteReconAsync(
            repo.Id,
            new ReconReport { ProposedAllow = ["src/Features/**"] },
            cancellationToken: TestContext.Current.CancellationToken);

        // The step still advances and the engineer is told; the alternative is a repository stuck in
        // recon because a third party had an outage.
        Assert.True(outcome.Succeeded);
        Assert.Equal(RepoStatus.Configuring, outcome.Status);
        Assert.Null(outcome.PullRequestNumber);
    }

    [Fact]
    public async Task RecordsOfOnboardingAreAttributableToANamedHuman()
    {
        await using var world = await OnboardingWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.Service.ConnectAsync(
            world.OrgId,
            4242,
            world.RepoName,
            actorUserId: world.UserId,
            cancellationToken: TestContext.Current.CancellationToken);

        await world.Service.StartReconAsync(
            repo.Id,
            world.UserId,
            TestContext.Current.CancellationToken);

        var entries = await world.Db.AuditLogs
            .Where(entry => entry.OrgId == world.OrgId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Section 7.3, guardrail 5: the agent never acts on its own initiative.
        Assert.Contains(entries, entry => entry.Action == OnboardingAuditActions.RepoConnected);
        Assert.Contains(entries, entry => entry.Action == OnboardingAuditActions.ReconStarted);
        Assert.All(entries, entry => Assert.Equal(world.UserId, entry.ActorUserId));
    }

    /// <summary>An organisation, a member and the onboarding service, against a throwaway database.</summary>
    private sealed class OnboardingWorld : IAsyncDisposable
    {
        private OnboardingWorld(CharterDbContext db, Guid orgId, Guid userId, Guid memberId, string repoName)
        {
            Db = db;
            OrgId = orgId;
            UserId = userId;
            MemberId = memberId;
            RepoName = repoName;

            Github = new FakeRepositoryClient { BranchHeads = { ["main"] = "basesha" } };

            // The default world has a merged, well-formed .charter/ on its base branch, so a test
            // asserting "no warnings" is asserting something about onboarding rather than about a
            // repository that happens to have no config yet.
            Folders = new FakeFolderLoader
            {
                Folder = CharterFolder.FromFiles(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [".charter/config.yml"] = "version: 1\nscopes:\n  allow:\n    - \"src/Features/**\"\n",
                    },
                    "basesha"),
            };
            Dispatcher = new RecordingDispatcher();

            Service = new OnboardingService(
                db,
                Github,
                Folders,
                Dispatcher,
                new AuditWriter(db, TimeProvider.System),
                TimeProvider.System,
                NullLogger<OnboardingService>.Instance);

            Query = new RequestableRepoQuery(db);
        }

        public CharterDbContext Db { get; }

        public Guid OrgId { get; }

        public Guid UserId { get; }

        public Guid MemberId { get; }

        public string RepoName { get; }

        public FakeRepositoryClient Github { get; }

        public FakeFolderLoader Folders { get; }

        public RecordingDispatcher Dispatcher { get; }

        public OnboardingService Service { get; }

        public RequestableRepoQuery Query { get; }

        public static async Task<OnboardingWorld?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);

            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the onboarding tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");

            var organization = Organization.Create($"onboarding-{tag}");
            var user = User.Create($"{tag}@example.test", "Test Engineer");
            var member = Charter.Domain.Member.Create(organization.Id, user.Id, Charter.Domain.Member.AllRoles);

            db.Organizations.Add(organization);
            db.Users.Add(user);
            db.Members.Add(member);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return new OnboardingWorld(db, organization.Id, user.Id, member.Id, $"acme/widgets-{tag[..8]}");
        }

        public Task<Repo> ConnectAsync()
            => Service.ConnectAsync(OrgId, 4242, RepoName, cancellationToken: TestContext.Current.CancellationToken);

        public async Task<Repo> ReachConfiguringAsync()
        {
            var repo = await ConnectAsync();

            await Service.StartReconAsync(repo.Id, cancellationToken: TestContext.Current.CancellationToken);
            await Service.CompleteReconAsync(
                repo.Id,
                new ReconReport { ProposedAllow = ["src/Features/**"] },
                cancellationToken: TestContext.Current.CancellationToken);

            return repo;
        }

        public async Task<Repo> ReachSmokeTestAsync()
        {
            var repo = await ReachConfiguringAsync();

            await Service.ConfirmScopeAsync(repo.Id, cancellationToken: TestContext.Current.CancellationToken);

            return repo;
        }

        public Task<IReadOnlyList<RequestableRepo>> RequestableAsync(params MemberRole[] roles)
            => Query.ListAsync(OrgId, MemberId, roles, TestContext.Current.CancellationToken);

        public Task<bool> CanFileAsync(Guid repoId, params MemberRole[] roles)
            => Query.CanFileAgainstAsync(OrgId, MemberId, roles, repoId, TestContext.Current.CancellationToken);

        public MemberSnapshot Member(params MemberRole[] roles) => new()
        {
            MemberId = MemberId,
            OrgId = OrgId,
            UserId = UserId,
            Roles = roles,
        };

        public RepoSnapshot Snapshot(Repo repo, bool canRequest) => new()
        {
            RepoId = repo.Id,
            OrgId = repo.OrgId,
            Status = repo.Status,
            Grants = canRequest ? [new RepoScopeGrant(null, MemberRole.Requester, true)] : [],
        };

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    /// <summary>A folder loader that answers whatever the test set.</summary>
    private sealed class FakeFolderLoader : ICharterFolderLoader
    {
        public CharterFolder Folder { get; set; } = CharterFolder.Missing("basesha");

        public Task<CharterFolder> LoadAsync(
            GitHubRepository repository,
            string commitSha,
            CancellationToken cancellationToken = default) => Task.FromResult(Folder);
    }

    /// <summary>A dispatcher that records rather than queues.</summary>
    private sealed class RecordingDispatcher : IOnboardingRunDispatcher
    {
        public List<OnboardingJobPayload> Recon { get; } = [];

        public List<OnboardingJobPayload> SmokeTest { get; } = [];

        public Task<Guid> DispatchReconAsync(
            OnboardingJobPayload payload,
            CancellationToken cancellationToken = default)
        {
            Recon.Add(payload with { ReadOnly = true });
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task<Guid> DispatchSmokeTestAsync(
            OnboardingJobPayload payload,
            CancellationToken cancellationToken = default)
        {
            SmokeTest.Add(payload with { ReadOnly = false });
            return Task.FromResult(Guid.CreateVersion7());
        }
    }
}
