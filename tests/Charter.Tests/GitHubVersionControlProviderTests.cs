using System.Net;
using System.Text.Json;
using Charter.Domain;
using Charter.GitHub;
using Charter.VersionControl;

namespace Charter.Tests;

/// <summary>
/// GitHub behind <see cref="IVersionControlProvider"/> (change spec 001 part A.4).
/// </summary>
/// <remarks>
/// Every call goes through a stubbed <c>HttpMessageHandler</c>. Nothing here reaches GitHub, so a
/// failure is always Charter's — and the assertions are as much about the request Charter sends as
/// about the answer it makes of the response.
/// </remarks>
public class GitHubVersionControlProviderTests
{
    [Fact]
    public async Task OpeningAChangeRequestPostsTheBranchesAndAppliesTheLabels()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(
                HttpMethod.Post,
                "/pulls",
                """
                {
                  "number": 17,
                  "html_url": "https://github.com/acme/widgets/pull/17",
                  "head": { "sha": "headsha", "ref": "charter/session-1" }
                }
                """)
            .On(HttpMethod.Post, "/labels", """[{"name":"unreviewed-spec"},{"name":"schema-change"}]""");

        var provider = VersionControlTestFixtures.GitHubProvider(handler);

        var snapshot = await provider.OpenChangeRequestAsync(
            new OpenChangeRequestCommand(
                VersionControlTestFixtures.RepoRef(),
                "charter/session-1",
                "main",
                "Add a thing",
                "body",
                ["unreviewed-spec", "schema-change"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(17, snapshot.Number);
        Assert.Equal(ChangeRequestState.Open, snapshot.State);
        Assert.Equal("headsha", snapshot.HeadRevision);
        Assert.Equal("charter/session-1", snapshot.SourceBranch);
        Assert.Equal("main", snapshot.TargetBranch);
        Assert.Equal(["unreviewed-spec", "schema-change"], snapshot.Labels);

        using var body = JsonDocument.Parse(handler.BodyFor("/pulls"));
        Assert.Equal("charter/session-1", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());

        using var labels = JsonDocument.Parse(handler.BodyFor("/labels"));
        Assert.Equal(2, labels.RootElement.GetProperty("labels").GetArrayLength());
    }

    [Fact]
    public async Task TheRecapIsPostedAsAnOrdinaryCommentAndNeverAsAReview()
    {
        // Section 14: the recap is an orientation aid, not a verdict. A review carries approve or
        // request-changes, and Charter has no standing to record either.
        var handler = VersionControlTestFixtures.Handler()
            .On(HttpMethod.Post, "/issues/17/comments", """{"id": 1}""");

        var provider = VersionControlTestFixtures.GitHubProvider(handler);

        var posted = await provider.CommentOnChangeRequestAsync(
            new ChangeRequestRef(VersionControlTestFixtures.RepoRef(), 17),
            "## What changed",
            TestContext.Current.CancellationToken);

        Assert.True(posted);
        Assert.Equal(1, handler.CountFor("/issues/17/comments"));
        Assert.Equal(0, handler.CountFor("/reviews"));
    }

    [Fact]
    public async Task StateComesBackAsMergedClosedDraftOrOpen()
    {
        Assert.Equal(ChangeRequestState.Merged, await State("""{"number":1,"state":"closed","merged":true}"""));
        Assert.Equal(ChangeRequestState.Closed, await State("""{"number":1,"state":"closed"}"""));
        Assert.Equal(ChangeRequestState.Draft, await State("""{"number":1,"state":"open","draft":true}"""));
        Assert.Equal(ChangeRequestState.Open, await State("""{"number":1,"state":"open"}"""));
    }

    [Fact]
    public async Task AChangeRequestThatIsNotThereIsNullRatherThanAnException()
    {
        var provider = VersionControlTestFixtures.GitHubProvider();

        var snapshot = await provider.GetChangeRequestStateAsync(
            new ChangeRequestRef(VersionControlTestFixtures.RepoRef(), 404),
            TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task AComparisonCarriesBothHalvesOfTheStalenessTest()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(
                HttpMethod.Get,
                "/compare/",
                """
                {
                  "ahead_by": 2,
                  "behind_by": 3,
                  "files": [
                    { "filename": "src/Widget.cs" },
                    { "filename": "src/Renamed.cs", "previous_filename": "src/Old.cs" }
                  ]
                }
                """);

        var comparison = await VersionControlTestFixtures.GitHubProvider(handler).CompareAsync(
            VersionControlTestFixtures.RepoRef(),
            "basesha",
            "headsha",
            TestContext.Current.CancellationToken);

        // GitHub's behind_by is "commits the base has that the head does not", which is what section
        // 17 means by behind — so the seam's BehindBy carries it unchanged.
        Assert.Equal(3, comparison.BehindBy);
        Assert.Equal(2, comparison.AheadBy);

        // Both sides of a rename, or an overlap against a renamed file reads as disjoint.
        Assert.Equal(["src/Widget.cs", "src/Renamed.cs", "src/Old.cs"], comparison.ChangedFiles);
    }

    [Fact]
    public async Task PushingAnAlreadyPublishedRevisionDoesNotWrite()
    {
        // The ordinary case after a restart. Publishing a ref twice is not an error, and not writing
        // is better than writing the same value.
        var handler = VersionControlTestFixtures.Handler()
            .On(HttpMethod.Get, "/git/ref/heads/charter/session-1", """{"object":{"sha":"headsha"}}""");

        var result = await VersionControlTestFixtures.GitHubProvider(handler).PushAsync(
            VersionControlTestFixtures.RepoRef(),
            "charter/session-1",
            "headsha",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal("headsha", result.Revision);
        Assert.Equal(0, handler.CountFor("/git/refs/heads/"));
    }

    [Fact]
    public async Task PushingToAMissingBranchCreatesIt()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(HttpMethod.Post, "/git/refs", """{"ref":"refs/heads/charter/session-1"}""");

        var result = await VersionControlTestFixtures.GitHubProvider(handler).PushAsync(
            VersionControlTestFixtures.RepoRef(),
            "charter/session-1",
            "newsha",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Equal(1, handler.CountFor("/git/refs"));
    }

    [Fact]
    public async Task ProtectionRequiringReviewIsReportedAsAMergeGate()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(
                HttpMethod.Get,
                "/branches/main/protection",
                """
                {
                  "required_pull_request_reviews": {
                    "required_approving_review_count": 1,
                    "require_code_owner_reviews": true,
                    "dismiss_stale_reviews": true
                  },
                  "enforce_admins": { "enabled": true }
                }
                """);

        var status = await VersionControlTestFixtures.GitHubProvider(handler).GetBranchProtectionAsync(
            VersionControlTestFixtures.RepoRef(),
            "main",
            TestContext.Current.CancellationToken);

        Assert.True(status.Configured);
        Assert.True(status.RequiresReview);
        Assert.Equal(1, status.RequiredApprovals);
        Assert.True(status.CodeOwnersReviewRequired);
        Assert.True(status.EnforcedForAdministrators);
    }

    [Fact]
    public async Task AnUnprotectedBranchIsReportedRatherThanThrown()
    {
        // GitHub answers 404 for "this branch has no protection rule". Part A.5 needs that as an
        // answer, not as an exception.
        var status = await VersionControlTestFixtures.GitHubProvider().GetBranchProtectionAsync(
            VersionControlTestFixtures.RepoRef(),
            "main",
            TestContext.Current.CancellationToken);

        Assert.False(status.Configured);
        Assert.False(status.RequiresReview);
        Assert.Contains("no branch protection rule", status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectionCharterMayNotReadIsNotVerifiedRatherThanProtected()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(HttpMethod.Get, "/branches/main/protection", "{}", HttpStatusCode.Forbidden);

        var status = await VersionControlTestFixtures.GitHubProvider(handler).GetBranchProtectionAsync(
            VersionControlTestFixtures.RepoRef(),
            "main",
            TestContext.Current.CancellationToken);

        Assert.False(status.Configured);
        Assert.Contains("not verified", status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectionThatOnlyBlocksForcePushesIsNotAMergeGate()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(HttpMethod.Get, "/branches/main/protection", """{"allow_force_pushes":{"enabled":false}}""");

        var status = await VersionControlTestFixtures.GitHubProvider(handler).GetBranchProtectionAsync(
            VersionControlTestFixtures.RepoRef(),
            "main",
            TestContext.Current.CancellationToken);

        Assert.True(status.Configured);
        Assert.False(status.RequiresReview);
    }

    [Fact]
    public async Task RegisteringAWebhookTwiceDoesNotProduceTwoDeliveries()
    {
        var handler = VersionControlTestFixtures.Handler()
            .On(
                HttpMethod.Get,
                "/hooks",
                """[{"id": 99, "config": {"url": "https://charter.example/api/github/webhook"}}]""");

        var registration = await VersionControlTestFixtures.GitHubProvider(handler).RegisterWebhookAsync(
            VersionControlTestFixtures.RepoRef(),
            new WebhookSubscription(
                new Uri("https://charter.example/api/github/webhook"),
                new Charter.Configuration.Secret("shh"),
                ["push", "change_request"]),
            TestContext.Current.CancellationToken);

        Assert.False(registration.Created);
        Assert.Equal("99", registration.ExternalId);
        Assert.Equal(0, handler.Calls.Count(call =>
            call.Method == "POST" && call.Path.Contains("/hooks", StringComparison.Ordinal)));
    }

    [Fact]
    public void CharterNeutralEventNamesTranslateToGitHubs()
    {
        Assert.Equal("pull_request", GitHubVersionControlProvider.GitHubEventName("change_request"));
        Assert.Equal("push", GitHubVersionControlProvider.GitHubEventName("push"));
    }

    [Fact]
    public void TheSeamStillCannotExpressAMerge()
    {
        // Section 7.4, restated at the seam: an interface that cannot say the word is a stronger
        // guarantee than a rule somebody has to remember, and it has to survive the abstraction.
        var methods = typeof(IVersionControlProvider).GetMethods().Select(method => method.Name);

        Assert.DoesNotContain(methods, name => name.Contains("Merge", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ChangeRequestState> State(string json)
    {
        var handler = VersionControlTestFixtures.Handler().On(HttpMethod.Get, "/pulls/1", json);

        var snapshot = await VersionControlTestFixtures.GitHubProvider(handler).GetChangeRequestStateAsync(
            new ChangeRequestRef(VersionControlTestFixtures.RepoRef(), 1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);

        return snapshot.State;
    }
}
