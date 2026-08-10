using System.Net;
using System.Text;
using System.Text.Json;
using Charter.GitHub;

namespace Charter.Tests;

/// <summary>
/// The repository content client: reads at a ref, one tree, one commit, one pull request.
/// </summary>
public class GitHubClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFileIsReadAtARefAndBase64Decoded()
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("version: 1\nbase_branch: main\n"));

        var handler = Handler()
            .On(HttpMethod.Get, "/contents/.charter/config.yml", $$"""{"sha":"blob1","content":"{{content}}"}""");

        var client = Client(handler);

        var file = await client.GetFileAsync(
            GitHubTestFixtures.Repository,
            ".charter/config.yml",
            "abc123",
            TestContext.Current.CancellationToken);

        Assert.NotNull(file);
        Assert.Equal("blob1", file.Sha);
        Assert.Contains("base_branch: main", file.Text, StringComparison.Ordinal);

        // The ref travels on the query string: reading "the config" without saying at which commit
        // is how a session ends up running under guardrails nobody committed.
        Assert.Contains("ref=abc123", handler.Calls[^1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFileIsNullRatherThanAnException()
    {
        // Section 9 onboards repositories that have never heard of Charter, so "not there" is an
        // ordinary answer and not a failure.
        var client = Client(Handler());

        var file = await client.GetFileAsync(
            GitHubTestFixtures.Repository,
            ".charter/config.yml",
            "abc123",
            TestContext.Current.CancellationToken);

        Assert.Null(file);
    }

    [Fact]
    public async Task AMissingBranchIsNullRatherThanAnException()
    {
        var client = Client(Handler());

        Assert.Null(await client.GetBranchHeadShaAsync(
            GitHubTestFixtures.Repository,
            "nope",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheTreeIsListedRecursively()
    {
        var handler = Handler()
            .On(
                HttpMethod.Get,
                "/git/trees/",
                """
                {
                  "tree": [
                    { "path": ".charter", "type": "tree", "sha": "t1" },
                    { "path": ".charter/config.yml", "type": "blob", "sha": "b1", "size": 120 },
                    { "path": ".charter/primer.md", "type": "blob", "sha": "b2" }
                  ]
                }
                """);

        var entries = await Client(handler).ListTreeAsync(
            GitHubTestFixtures.Repository,
            "abc123",
            TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);
        Assert.Equal(2, entries.Count(entry => entry.IsBlob));
        Assert.Equal(120, entries[1].Size);
        Assert.Contains("recursive=1", handler.Calls.Last().Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritingSeveralFilesProducesExactlyOneCommit()
    {
        var handler = Handler()
            .On(HttpMethod.Get, "/git/ref/heads/charter/onboarding", """{"object":{"sha":"headsha"}}""")
            .On(HttpMethod.Get, "/git/commits/headsha", """{"tree":{"sha":"basetree"}}""")
            .On(HttpMethod.Post, "/git/blobs", """{"sha":"blobsha"}""")
            .On(HttpMethod.Post, "/git/trees", """{"sha":"newtree"}""")
            .On(HttpMethod.Post, "/git/commits", """{"sha":"newcommit"}""")
            .On(HttpMethod.Patch, "/git/refs/heads/charter/onboarding", """{"object":{"sha":"newcommit"}}""");

        var result = await Client(handler).CommitFilesAsync(
            GitHubTestFixtures.Repository,
            "charter/onboarding",
            "Add Charter guardrails",
            [
                new GitHubFileEdit(".charter/config.yml", "version: 1\n"),
                new GitHubFileEdit(".charter/conventions.md", "# Conventions\n"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal("newcommit", result.Sha);

        // Two blobs, one tree, one commit, one ref update. The contents API would have produced a
        // commit per file and turned a two-file proposal into a two-commit pull request.
        Assert.Equal(2, handler.CountFor("/git/blobs"));
        Assert.Equal(1, handler.CountFor("/git/trees"));
        Assert.Equal(1, handler.CountFor("/git/commits/"));
        Assert.Equal(1, handler.Calls.Count(call => call.Method == "POST" && call.Path.EndsWith("/git/commits", StringComparison.Ordinal)));

        using var tree = JsonDocument.Parse(handler.BodyFor("/git/trees"));
        Assert.Equal("basetree", tree.RootElement.GetProperty("base_tree").GetString());
        Assert.Equal(2, tree.RootElement.GetProperty("tree").GetArrayLength());
    }

    [Fact]
    public async Task CommittingToABranchThatDoesNotExistFailsClearly()
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => Client(Handler()).CommitFilesAsync(
                GitHubTestFixtures.Repository,
                "charter/onboarding",
                "message",
                [new GitHubFileEdit("a.txt", "a")],
                TestContext.Current.CancellationToken));

        Assert.Contains("charter/onboarding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABranchIsCreatedAtACommit()
    {
        var handler = Handler().On(HttpMethod.Post, "/git/refs", """{"ref":"refs/heads/charter/onboarding"}""");

        await Client(handler).CreateBranchAsync(
            GitHubTestFixtures.Repository,
            "charter/onboarding",
            "basesha",
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.BodyFor("/git/refs"));

        Assert.Equal("refs/heads/charter/onboarding", body.RootElement.GetProperty("ref").GetString());
        Assert.Equal("basesha", body.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task APullRequestIsOpenedAgainstTheBaseBranch()
    {
        var handler = Handler()
            .On(
                HttpMethod.Post,
                "/pulls",
                """
                {
                  "number": 17,
                  "html_url": "https://github.com/acme/widgets/pull/17",
                  "head": { "sha": "headsha" }
                }
                """);

        var pullRequest = await Client(handler).OpenPullRequestAsync(
            GitHubTestFixtures.Repository,
            "charter/onboarding",
            "main",
            "Charter: propose guardrails",
            "body",
            TestContext.Current.CancellationToken);

        Assert.Equal(17, pullRequest.Number);
        Assert.Equal("https://github.com/acme/widgets/pull/17", pullRequest.Url);

        using var body = JsonDocument.Parse(handler.BodyFor("/pulls"));
        Assert.Equal("charter/onboarding", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
    }

    [Fact]
    public void TheClientCannotExpressAMerge()
    {
        // Section 7.4: Charter has no merge button, and an interface that cannot say the word is a
        // stronger guarantee than a rule somebody has to remember.
        var methods = typeof(IGitHubRepositoryClient).GetMethods().Select(method => method.Name).ToList();

        Assert.DoesNotContain(methods, name => name.Contains("Merge", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnExpiredTokenIsDroppedAndTheCallRetriedOnce()
    {
        var clock = new ModelFakeTimeProvider(Now);

        var handler = new GitHubStubHandler()
            .Once(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_stale", Now.AddHours(1)))
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_fresh", Now.AddHours(2)))
            .Once(HttpMethod.Get, "/git/ref/heads/main", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized)
            .On(HttpMethod.Get, "/git/ref/heads/main", """{"object":{"sha":"abc123"}}""");

        using var provider = GitHubTestFixtures.Provider(handler, clock);
        var client = GitHubTestFixtures.Client(handler, provider);

        var sha = await client.GetBranchHeadShaAsync(
            GitHubTestFixtures.Repository,
            "main",
            TestContext.Current.CancellationToken);

        Assert.Equal("abc123", sha);

        // A revoked installation or a rotated key looks exactly like this, and re-minting once is
        // the only retry worth having: retrying a write on anything else risks committing twice.
        Assert.Equal(2, handler.CountFor("/access_tokens"));
        Assert.Equal(2, handler.CountFor("/git/ref/heads/main"));
    }

    [Fact]
    public async Task APersistentAuthFailureGivesUpRatherThanLooping()
    {
        var handler = new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_a", Now.AddHours(1)))
            .On(HttpMethod.Post, "/pulls", """{"message":"Forbidden"}""", HttpStatusCode.Forbidden);

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));
        var client = GitHubTestFixtures.Client(handler, provider);

        await Assert.ThrowsAsync<GitHubApiException>(
            () => client.OpenPullRequestAsync(
                GitHubTestFixtures.Repository,
                "charter/onboarding",
                "main",
                "title",
                "body",
                TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.CountFor("/pulls"));
    }

    private static GitHubStubHandler Handler()
        => new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_test", Now.AddHours(1)));

    private static GitHubRepositoryClient Client(GitHubStubHandler handler)
        => GitHubTestFixtures.Client(handler, GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now)));
}
