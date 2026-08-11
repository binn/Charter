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

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("https://admin:hunter2@preview.example.com/")]
    [InlineData("file:///etc/passwd")]
    public async Task AReportNamingAUrlCharterWillNotVouchForIsRefusedAndNothingIsStored(string url)
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var ingestor = fixture.Ingestor(Options(), handler: Reachable());

        var result = await ingestor.ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest(url, "ready", "render"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentBindingOutcome.UnsafeUrl, result.Outcome);

        // Refuse, do not sanitise (section 16.3). The URL is not stored anywhere, so no later consumer
        // can pick it up — the whole reason the check is at the write path rather than at each read.
        var deployment = await fixture.Db.Deployments
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(deployment.Url);
        Assert.Equal(DeploymentState.Failed, deployment.State);
    }

    [Fact]
    public async Task AHostnameThatResolvesInsideTheNetworkIsRefusedToo()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // The case no rule about the URL text can catch: it reads like every other preview link.
        fixture.Resolver.Map("preview.northbeam.example", "169.254.169.254");

        var result = await fixture.Ingestor(Options(), handler: Reachable()).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://preview.northbeam.example/", "ready", "render"),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentBindingOutcome.UnsafeUrl, result.Outcome);
    }

    [Fact]
    public async Task ARefusedUrlLeavesTheRequesterWithAnHonestCardRatherThanASpinnerOrALink()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Ingestor(Options(), handler: Reachable()).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("http://169.254.169.254/", "ready", "render"),
            TestContext.Current.CancellationToken);

        var artifact = await fixture.PreviewAsync();

        // Section 11: failure has dignity. The card settles on the designed failed state — no button,
        // no "Nothing you do here touches the real one" above a link Charter never checked, and no
        // skeleton spinning forever on a preview that is not coming.
        Assert.NotNull(artifact);
        Assert.Equal(VerificationArtifactState.Failed, artifact.State);
        Assert.Null(artifact.Url);
    }

    [Fact]
    public async Task ACommentNamingAnAddressInsideTheNetworkIsRefusedOnTheSamePath()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Anybody who can comment on a change request can write this one. Both ingestion paths run
        // through the same binder, which is what makes one gate enough.
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

        var result = await fixture.Ingestor(options, new DeploymentProviderRegistry([railway]), Reachable())
            .IngestCommentAsync(
                DeploymentScenario.RepoFullName,
                DeploymentScenario.Number,
                new DeploymentComment("railway[bot]", "✅ Deploy successful — http://169.254.169.254/"),
                TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentBindingOutcome.UnsafeUrl, result.Outcome);
        Assert.Null((await fixture.PreviewAsync())?.Url);
    }

    [Fact]
    public async Task ARowWrittenBeforeTheCheckExistedIsStillRefusedWhenTheCardIsPublished()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 16.3: a fix at the recording point cannot reach rows already in the database, and an
        // upgrade does not rewrite them. This is such a row — written straight to the table, the way an
        // older Charter wrote it — and the point of use has to refuse it on its own.
        fixture.Db.Deployments.Add(Deployment.Report(
            fixture.Scenario.ChangeRequest.Id,
            "render",
            DeploymentState.Ready,
            "http://169.254.169.254/latest/meta-data/",
            fixture.Clock.Now));

        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Db.ChangeTracker.Clear();

        await fixture.Ingestor(Options(), handler: Reachable())
            .PublishAsync(DeploymentScenario.HeadSha, TestContext.Current.CancellationToken);

        var artifact = await fixture.PreviewAsync();

        Assert.Equal(VerificationArtifactState.Failed, artifact?.State);
        Assert.Null(artifact?.Url);
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
