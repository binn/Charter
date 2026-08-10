using System.Net;
using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Section 18's two ingestion paths, end to end against a real database, ending in the artifact a
/// requester clicks.
/// </summary>
public class DeploymentBindingTests
{
    private static DeploymentOptions Options(int ttlHours = 8) => new()
    {
        Provider = DeploymentProviderKind.None,
        PreviewTtl = TimeSpan.FromHours(ttlHours),
    };

    private static StubHttpMessageHandler Reachable()
        => new StubHttpMessageHandler().EnqueueJson("<html></html>");

    private static DeploymentWebhookRequest Ready(string url = "https://quote-tool-pr-142.up.railway.app")
        => new(url, "ready", "railway");

    [Fact]
    public async Task AReportBoundToTheHeadCommitProducesTheHostedPreviewArtifact()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var options = Options();
        var ingestor = fixture.Ingestor(options, handler: Reachable());

        var result = await ingestor.ReportAsync(
            DeploymentScenario.HeadSha,
            Ready(),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentBindingOutcome.Recorded, result.Outcome);

        var artifact = await fixture.PreviewAsync();

        Assert.NotNull(artifact);
        Assert.Equal(VerificationArtifactKind.HostedPreview, artifact.Kind);
        Assert.Equal(VerificationArtifactState.Ready, artifact.State);

        // Section 27.4 puts web at the top of the table where the requester clicks a link and gets the
        // whole loop. An engineer_only preview would remove the one class where that holds.
        Assert.Equal(VerificationArtifactAudience.Requester, artifact.Audience);
        Assert.Equal("https://quote-tool-pr-142.up.railway.app", artifact.Url);
        Assert.Equal(fixture.Clock.Now.AddHours(8), artifact.ExpiresAt);
        Assert.NotNull(artifact.InstructionsMd);
    }

    [Fact]
    public async Task AReportForACommitNobodyHasIsRefusedAndProducesNothing()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var ingestor = fixture.Ingestor(Options(), handler: Reachable());

        var result = await ingestor.ReportAsync(
            "0000000000000000000000000000000000000000",
            Ready(),
            TestContext.Current.CancellationToken);

        // The head commit is the authorisation. An unknown one is a 404 at the endpoint, and nothing
        // is written — the report cannot attach a URL to work it does not name.
        Assert.Equal(DeploymentBindingOutcome.UnknownCommit, result.Outcome);
        Assert.Null(await fixture.PreviewAsync());
    }

    [Fact]
    public async Task ARedeployPutsTheCardBackToPendingRatherThanLeavingADeadLinkUp()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var options = Options();
        var ingestor = fixture.Ingestor(options, handler: Reachable());

        await ingestor.ReportAsync(DeploymentScenario.HeadSha, Ready(), TestContext.Current.CancellationToken);

        var ready = await fixture.PreviewAsync();
        Assert.Equal(VerificationArtifactState.Ready, ready?.State);

        await fixture.Ingestor(options, handler: Reachable()).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest(null, "building", "railway"),
            TestContext.Current.CancellationToken);

        var rebuilding = await fixture.PreviewAsync();

        Assert.Equal(VerificationArtifactState.Pending, rebuilding?.State);
        Assert.Null(rebuilding?.Url);
        Assert.Null(rebuilding?.ExpiresAt);
    }

    [Theory]
    [InlineData("failed", VerificationArtifactState.Failed)]
    [InlineData("cancelled", VerificationArtifactState.Failed)]
    [InlineData("expired", VerificationArtifactState.Expired)]
    [InlineData("queued", VerificationArtifactState.Pending)]
    public async Task ADeploymentStateBecomesTheCardsState(string reported, VerificationArtifactState expected)
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var ingestor = fixture.Ingestor(Options(), handler: Reachable());

        await ingestor.ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest(null, reported, "railway"),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, (await fixture.PreviewAsync())?.State);
    }

    [Fact]
    public async Task TheExpiryIsStampedOnceAndNotPushedForwardByEveryReconcile()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var options = Options();

        await fixture.Ingestor(options, handler: Reachable()).ReportAsync(
            DeploymentScenario.HeadSha,
            Ready(),
            TestContext.Current.CancellationToken);

        var first = await fixture.PreviewAsync();

        // An hour later, the same preview is reported again — a reconcile pass, or a redelivery.
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);

        await fixture.Ingestor(options, handler: Reachable()).ReportAsync(
            DeploymentScenario.HeadSha,
            Ready(),
            TestContext.Current.CancellationToken);

        var second = await fixture.PreviewAsync();

        // Section 27.7's countdown is the mitigation for the confusion expiry causes. A clock that
        // silently restarts every pass is not a countdown.
        Assert.Equal(first?.ExpiresAt, second?.ExpiresAt);
    }

    [Fact]
    public async Task AnUnreachablePreviewIsRecordedAsUnreachableRatherThanGuessedAt()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var handler = new StubHttpMessageHandler().EnqueueError(HttpStatusCode.BadGateway, "no upstream");

        await fixture.Ingestor(Options(), handler: handler).ReportAsync(
            DeploymentScenario.HeadSha,
            Ready(),
            TestContext.Current.CancellationToken);

        var artifact = await fixture.PreviewAsync();

        Assert.NotNull(artifact?.Payload);
        Assert.Contains("unreachable", artifact.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommentFromTheBotBindsThroughTheSameBinderAsTheWebhook()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var options = new DeploymentOptions
        {
            Provider = DeploymentProviderKind.Railway,
            PreviewTtl = TimeSpan.FromHours(8),
            Railway = new RailwayOptions
            {
                Token = Charter.Configuration.Secret.From("railway-token")!,
                ProjectId = "proj_123",
                BaseEnvironment = "staging",
                ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
            },
        };

        var railway = new RailwayDeploymentProvider(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            options,
            fixture.Clock,
            new RecordingLogger<RailwayDeploymentProvider>());

        var ingestor = fixture.Ingestor(options, new DeploymentProviderRegistry([railway]), Reachable());

        var result = await ingestor.IngestCommentAsync(
            DeploymentScenario.RepoFullName,
            DeploymentScenario.Number,
            new DeploymentComment(
                "railway[bot]",
                "✅ Deploy successful — https://quote-tool-pr-142.up.railway.app"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentBindingOutcome.Recorded, result.Outcome);

        var artifact = await fixture.PreviewAsync();

        Assert.Equal(VerificationArtifactState.Ready, artifact?.State);
        Assert.Equal("https://quote-tool-pr-142.up.railway.app/", artifact?.Url);

        var deployment = await fixture.Db.Deployments
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("railway", deployment.Provider);
        Assert.Equal(DeploymentState.Ready, deployment.State);
    }

    [Fact]
    public async Task ACommentThatIsAboutSomethingElseChangesNothing()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var options = new DeploymentOptions
        {
            Provider = DeploymentProviderKind.Railway,
            PreviewTtl = TimeSpan.FromHours(8),
            Railway = new RailwayOptions
            {
                Token = Charter.Configuration.Secret.From("railway-token")!,
                ProjectId = "proj_123",
                BaseEnvironment = "staging",
                ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
            },
        };

        var railway = new RailwayDeploymentProvider(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            options,
            fixture.Clock,
            new RecordingLogger<RailwayDeploymentProvider>());

        var ingestor = fixture.Ingestor(options, new DeploymentProviderRegistry([railway]), Reachable());

        var result = await ingestor.IngestCommentAsync(
            DeploymentScenario.RepoFullName,
            DeploymentScenario.Number,
            new DeploymentComment("ayesha", "I still think the second step reads oddly."),
            TestContext.Current.CancellationToken);

        // Not an error. Nothing happened, which is what most comments mean.
        Assert.Equal(DeploymentBindingOutcome.Invalid, result.Outcome);
        Assert.Null(await fixture.PreviewAsync());
        Assert.Empty(await fixture.Db.Deployments.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AChangeRequestIsFoundByRepositoryAndNumber()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var ingestor = fixture.Ingestor(Options());

        var found = await ingestor.FindAsync(
            DeploymentScenario.RepoFullName,
            DeploymentScenario.Number,
            TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(DeploymentScenario.HeadSha, found.Target.HeadSha);
        Assert.Equal(DeploymentScenario.HeadBranch, found.Target.HeadBranch);
        Assert.Equal(fixture.SessionId, found.SessionId);

        Assert.Null(await ingestor.FindAsync("someone/else", 142, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMergedChangeRequestIsNoLongerWorthAskingAbout()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(state: ChangeRequestState.Merged);
        if (fixture is null)
        {
            return;
        }

        var open = await fixture.Ingestor(Options()).OpenAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(open, context => context.SessionId == fixture.SessionId);
    }

    [Fact]
    public async Task AnOpenChangeRequestIsStillWorthAskingAbout()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var open = await fixture.Ingestor(Options()).OpenAsync(TestContext.Current.CancellationToken);

        Assert.Contains(open, context => context.SessionId == fixture.SessionId);
    }
}
