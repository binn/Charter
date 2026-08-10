using Charter.Configuration;
using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;

namespace Charter.Tests;

/// <summary>
/// Section 18's comment fallback, from the signed webhook body all the way to the artifact.
/// </summary>
/// <remarks>
/// The parser and the binding were tested in isolation long before anything could reach them: the
/// delivery record carried no comment fields, so a Railway bot comment arriving at the webhook was
/// parsed into a record that dropped it on the floor. These tests exercise the join — raw
/// <c>issue_comment</c> JSON through <see cref="GitHubWebhookDelivery.Parse"/>, through
/// <see cref="DeploymentCommentListener"/>, to a <c>hosted_preview</c> a requester can click.
/// </remarks>
public class GitHubCommentDeliveryTests
{
    private static DeploymentOptions RailwayOptionsFor()
        => new()
        {
            Provider = DeploymentProviderKind.Railway,
            PreviewTtl = TimeSpan.FromHours(8),
            Railway = new RailwayOptions
            {
                Token = Secret.From("railway-token")!,
                ProjectId = "proj_123",
                BaseEnvironment = "staging",
                ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
            },
        };

    private static string CommentDelivery(string author, string body, int number = DeploymentScenario.Number)
        => $$"""
             {
               "action": "created",
               "issue": { "number": {{number}}, "pull_request": { "url": "https://api.github.com/x" } },
               "comment": {
                 "user": { "login": {{System.Text.Json.JsonSerializer.Serialize(author)}} },
                 "body": {{System.Text.Json.JsonSerializer.Serialize(body)}}
               },
               "repository": { "full_name": "{{DeploymentScenario.RepoFullName}}" }
             }
             """;

    private static (DeploymentCommentListener Listener, DeploymentIngestor Ingestor) Wire(DeploymentFixture fixture)
    {
        var options = RailwayOptionsFor();

        var railway = new RailwayDeploymentProvider(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            options,
            fixture.Clock,
            new RecordingLogger<RailwayDeploymentProvider>());

        var ingestor = fixture.Ingestor(
            options,
            new DeploymentProviderRegistry([railway]),
            new StubHttpMessageHandler().EnqueueJson("<html></html>"));

        return (new DeploymentCommentListener(ingestor, new RecordingLogger<DeploymentCommentListener>()), ingestor);
    }

    [Fact]
    public async Task ABotCommentDeliveredByWebhookBecomesTheRequestersPreview()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (listener, _) = Wire(fixture);

        var delivery = GitHubWebhookDelivery.Parse(
            "issue_comment",
            CommentDelivery(
                "railway[bot]",
                "🚅 Deploy successful — https://quote-tool-pr-142.up.railway.app"));

        await listener.OnDeliveryAsync(delivery, TestContext.Current.CancellationToken);

        var artifact = await fixture.PreviewAsync();

        Assert.Equal(VerificationArtifactState.Ready, artifact?.State);
        Assert.Equal("https://quote-tool-pr-142.up.railway.app/", artifact?.Url);
    }

    [Fact]
    public async Task AHumanTalkingOnTheChangeRequestChangesNothing()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (listener, _) = Wire(fixture);

        // Not an error, and it must never be treated as one: this is what almost every comment on a
        // change request looks like.
        var delivery = GitHubWebhookDelivery.Parse(
            "issue_comment",
            CommentDelivery("ayesha", "looks right to me, try https://example.com when you get a sec"));

        await listener.OnDeliveryAsync(delivery, TestContext.Current.CancellationToken);

        Assert.Null(await fixture.PreviewAsync());
    }

    [Fact]
    public async Task ACommentOnAChangeRequestCharterDidNotOpenIsIgnored()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (listener, _) = Wire(fixture);

        var delivery = GitHubWebhookDelivery.Parse(
            "issue_comment",
            CommentDelivery(
                "railway[bot]",
                "Deploy successful — https://someone-elses-pr.up.railway.app",
                number: DeploymentScenario.Number + 1_000));

        await listener.OnDeliveryAsync(delivery, TestContext.Current.CancellationToken);

        Assert.Null(await fixture.PreviewAsync());
    }

    [Fact]
    public async Task ADeletedCommentIsNotReReadAsAnAnnouncement()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (listener, _) = Wire(fixture);

        var json = CommentDelivery(
                "railway[bot]",
                "Deploy successful — https://quote-tool-pr-142.up.railway.app")
            .Replace("\"created\"", "\"deleted\"", StringComparison.Ordinal);

        await listener.OnDeliveryAsync(
            GitHubWebhookDelivery.Parse("issue_comment", json),
            TestContext.Current.CancellationToken);

        Assert.Null(await fixture.PreviewAsync());
    }

    [Fact]
    public async Task AnEventThatIsNotAComentIsLeftToTheOtherListeners()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (listener, _) = Wire(fixture);

        await listener.OnDeliveryAsync(
            GitHubWebhookDelivery.Parse("push", """{"ref":"refs/heads/main","after":"abc"}"""),
            TestContext.Current.CancellationToken);

        Assert.Null(await fixture.PreviewAsync());
    }
}
