using System.Security.Cryptography;
using System.Text;
using Charter.Domain;
using Charter.GitHub;

namespace Charter.Tests;

/// <summary>
/// The webhook receiver: signature verification first, parsing second.
/// </summary>
/// <remarks>
/// The webhook route is the one endpoint on a Charter instance that anybody on the internet can
/// reach and that acts on what it is told, so these are the tests that matter most in this file set.
/// </remarks>
public class GitHubWebhookSignatureTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"action":"created"}""");

    [Fact]
    public void AGenuineSignatureIsAccepted()
    {
        var signature = GitHubWebhookSignature.Compute(GitHubTestFixtures.WebhookSecret, Payload);

        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
        Assert.True(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, signature));
    }

    [Fact]
    public void TheDigestIsHmacSha256OverTheExactBytes()
    {
        var expected = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(GitHubTestFixtures.WebhookSecret), Payload));

        Assert.Equal(expected, GitHubWebhookSignature.Compute(GitHubTestFixtures.WebhookSecret, Payload));
    }

    [Fact]
    public void AWrongSignatureIsRejected()
    {
        var wrong = GitHubWebhookSignature.Compute("a different secret entirely", Payload);

        Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, wrong));
    }

    [Fact]
    public void ASignatureForDifferentBytesIsRejected()
    {
        var signature = GitHubWebhookSignature.Compute(GitHubTestFixtures.WebhookSecret, Payload);
        var tampered = Encoding.UTF8.GetBytes("""{"action":"deleted"}""");

        Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, tampered, signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingHeaderIsRejected(string? header)
        => Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, header));

    [Fact]
    public void AHeaderWithoutTheSha256PrefixIsRejected()
    {
        // The SHA-1 X-Hub-Signature header exists and is not accepted: a receiver that takes either
        // accepts the weaker one, which is the same as only accepting the weaker one.
        var bare = GitHubWebhookSignature.Compute(GitHubTestFixtures.WebhookSecret, Payload)["sha256=".Length..];

        Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, bare));
        Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, "sha1=" + bare));
    }

    [Theory]
    [InlineData("sha256=")]
    [InlineData("sha256=deadbeef")]
    [InlineData("sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AMalformedDigestIsRejectedRatherThanThrowing(string header)
        => Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, header));

    [Fact]
    public void VerificationIsConstantTimeByConstruction()
    {
        // Not a timing measurement — those are flaky. The assertion is structural: the only
        // comparison in the verifier is CryptographicOperations.FixedTimeEquals, so a digest that
        // shares a long prefix with the real one is no closer to being accepted than one that
        // shares nothing.
        var real = GitHubWebhookSignature.Compute(GitHubTestFixtures.WebhookSecret, Payload);
        var nearMiss = string.Concat(real.AsSpan(0, real.Length - 1), real[^1] == 'a' ? "b" : "a");

        Assert.False(GitHubWebhookSignature.IsValid(GitHubTestFixtures.WebhookSecret, Payload, nearMiss));
        Assert.False(GitHubWebhookSignature.IsValid(
            GitHubTestFixtures.WebhookSecret,
            Payload,
            "sha256=" + new string('0', 64)));
    }

    [Fact]
    public void TheSourceContainsNoOrdinaryStringComparisonOfDigests()
    {
        // Guards the property above against a future edit: string.Equals on the header would pass
        // every test in this class and quietly reintroduce the timing oracle.
        var source = File.ReadAllText(SourcePath("src/Charter/GitHub/GitHubWebhookSignature.cs"));

        Assert.Contains("CryptographicOperations.FixedTimeEquals", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(expected", source, StringComparison.Ordinal);
        Assert.DoesNotContain("presented == expected", source, StringComparison.Ordinal);
    }

    internal static string SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Charter.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, relative);
    }
}

/// <summary>Parsing a verified delivery down to the facts Charter acts on.</summary>
public class GitHubWebhookDeliveryTests
{
    [Fact]
    public void AnInstallationDeliveryNamesItsRepositories()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "installation",
            """
            {
              "action": "created",
              "installation": { "id": 4242 },
              "repositories": [
                { "full_name": "acme/widgets" },
                { "full_name": "acme/other" }
              ]
            }
            """,
            "delivery-1");

        Assert.Equal(GitHubWebhookEventType.Installation, delivery.Type);
        Assert.Equal("created", delivery.Action);
        Assert.Equal(4242, delivery.InstallationId);
        Assert.Equal(["acme/widgets", "acme/other"], delivery.RepositoryFullNames);
        Assert.Equal("delivery-1", delivery.DeliveryId);
    }

    [Fact]
    public void AnInstallationRepositoriesDeliveryReadsAddedAndRemoved()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "installation_repositories",
            """
            {
              "action": "removed",
              "installation": { "id": 7 },
              "repositories_removed": [ { "full_name": "acme/gone" } ]
            }
            """);

        Assert.Equal(GitHubWebhookEventType.InstallationRepositories, delivery.Type);
        Assert.Equal(["acme/gone"], delivery.RepositoryFullNames);
    }

    [Fact]
    public void APushCarriesItsBranchAndHeadCommit()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "push",
            """
            {
              "ref": "refs/heads/main",
              "after": "abc1234",
              "repository": { "full_name": "acme/widgets", "default_branch": "main" },
              "installation": { "id": 4242 }
            }
            """);

        Assert.Equal(GitHubWebhookEventType.Push, delivery.Type);
        Assert.Equal("main", delivery.Branch);
        Assert.Equal("abc1234", delivery.HeadSha);
        Assert.Equal("acme/widgets", delivery.RepositoryFullName);
    }

    [Fact]
    public void ATagPushHasNoBranch()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "push",
            """{"ref":"refs/tags/v1.0.0","after":"abc","repository":{"full_name":"acme/widgets"}}""");

        Assert.Null(delivery.Branch);
    }

    [Fact]
    public void APullRequestCarriesItsNumberStateAndBranches()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "pull_request",
            """
            {
              "action": "closed",
              "pull_request": {
                "number": 17,
                "merged": true,
                "head": { "ref": "charter/onboarding", "sha": "headsha" },
                "base": { "ref": "main" }
              },
              "repository": { "full_name": "acme/widgets" }
            }
            """);

        Assert.Equal(GitHubWebhookEventType.PullRequest, delivery.Type);
        Assert.Equal(17, delivery.PullRequestNumber);
        Assert.True(delivery.PullRequestMerged);
        Assert.Equal("charter/onboarding", delivery.PullRequestHeadBranch);
        Assert.Equal("main", delivery.PullRequestBaseBranch);
        Assert.Equal("headsha", delivery.HeadSha);
    }

    [Fact]
    public void ACheckSuiteCarriesItsConclusion()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "check_suite",
            """
            {
              "action": "completed",
              "check_suite": { "head_sha": "abc", "conclusion": "success" },
              "repository": { "full_name": "acme/widgets" }
            }
            """);

        Assert.Equal(GitHubWebhookEventType.CheckSuite, delivery.Type);
        Assert.True(delivery.CheckSuiteSucceeded);
        Assert.Equal("abc", delivery.HeadSha);
    }

    [Fact]
    public void AnUnhandledEventIsAcknowledgedRatherThanRefused()
    {
        var delivery = GitHubWebhookDelivery.Parse("star", """{"action":"created"}""");

        Assert.Equal(GitHubWebhookEventType.Unknown, delivery.Type);
        Assert.Equal("star", delivery.EventName);
    }

    [Fact]
    public void ASignedBodyThatIsNotJsonDoesNotThrow()
    {
        var delivery = GitHubWebhookDelivery.Parse("push", "this is not json");

        Assert.Equal(GitHubWebhookEventType.Unknown, delivery.Type);
        Assert.Null(delivery.RepositoryFullName);
    }

    [Fact]
    public void MissingFieldsAreNullRatherThanExceptions()
    {
        var delivery = GitHubWebhookDelivery.Parse("pull_request", "{}");

        Assert.Null(delivery.PullRequestNumber);
        Assert.Null(delivery.InstallationId);
        Assert.Null(delivery.RepositoryFullName);
        Assert.Empty(delivery.RepositoryFullNames);
    }
}

/// <summary>The section 18 generic deployment webhook's vocabulary.</summary>
public class GitHubDeploymentWebhookTests
{
    [Theory]
    [InlineData("ready", DeploymentState.Ready)]
    [InlineData("success", DeploymentState.Ready)]
    [InlineData("deployed", DeploymentState.Ready)]
    [InlineData("building", DeploymentState.Building)]
    [InlineData("deploying", DeploymentState.Building)]
    [InlineData("queued", DeploymentState.Pending)]
    [InlineData("error", DeploymentState.Failed)]
    [InlineData("canceled", DeploymentState.Cancelled)]
    [InlineData("removed", DeploymentState.Expired)]
    [InlineData("READY", DeploymentState.Ready)]
    public void ProviderVocabularyMapsOntoCharterStates(string reported, DeploymentState expected)
    {
        Assert.True(DeploymentBinder.TryParseState(reported, out var state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void AnUnknownStateIsRefusedRatherThanGuessed(string? reported)
        => Assert.False(DeploymentBinder.TryParseState(reported, out _));
}
