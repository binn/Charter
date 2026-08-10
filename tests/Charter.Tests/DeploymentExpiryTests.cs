using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Expiry as a designed state rather than a 404 (section 27.7).
/// </summary>
/// <remarks>
/// Section 27.7 names this the number one source of confusion in tools like this: somebody opens a
/// link from a three-day-old notification, gets a dead host, and reports the product as broken. What
/// prevents that is a countdown visible from first render and an <c>expired</c> state with a
/// <c>Rebuild</c> button — not a working link that quietly stops working.
/// </remarks>
public class DeploymentExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static DeploymentOptions Options() => new()
    {
        Provider = DeploymentProviderKind.None,
        PreviewTtl = TimeSpan.FromHours(8),
    };

    [Fact]
    public void TheRenderedStateFollowsTheClockWithoutAnythingHavingToRun()
    {
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), VerificationArtifactKind.HostedPreview, now: Now);
        artifact.MarkReady(url: "https://preview.example.com", expiresAt: Now.AddHours(8));

        Assert.Equal(VerificationArtifactState.Ready, artifact.DisplayStateAt(Now));
        Assert.Equal(VerificationArtifactState.Ready, artifact.DisplayStateAt(Now.AddHours(6)));

        // Under an hour: section 27.7 turns the countdown amber.
        Assert.Equal(VerificationArtifactState.Expiring, artifact.DisplayStateAt(Now.AddHours(7).AddMinutes(30)));
        Assert.Equal(VerificationArtifactState.Expiring, artifact.DisplayStateAt(Now.AddHours(8).AddSeconds(-1)));

        // Past it: the body is replaced and the primary action becomes Rebuild. Never a dead link.
        Assert.Equal(VerificationArtifactState.Expired, artifact.DisplayStateAt(Now.AddHours(8)));
        Assert.Equal(VerificationArtifactState.Expired, artifact.DisplayStateAt(Now.AddDays(3)));
    }

    [Fact]
    public void APreviewWithNoExpiryNeverExpires()
    {
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), VerificationArtifactKind.HostedPreview, now: Now);
        artifact.MarkReady(url: "https://preview.example.com");

        Assert.Equal(VerificationArtifactState.Ready, artifact.DisplayStateAt(Now.AddYears(1)));
    }

    [Fact]
    public void ARebuildClearsTheDeadHostRatherThanRenderingASkeletonAboveIt()
    {
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), VerificationArtifactKind.HostedPreview, now: Now);
        artifact.MarkReady(url: "https://preview.example.com", expiresAt: Now.AddHours(1));
        artifact.MarkExpired();

        artifact.MarkPending();

        Assert.Equal(VerificationArtifactState.Pending, artifact.State);
        Assert.Null(artifact.Url);
        Assert.Null(artifact.ExpiresAt);
    }

    [Fact]
    public async Task ExpiryIsQueryableBeforeItHappensAndAfterwards()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        await fixture.Ingestor(Options(), handler: new StubHttpMessageHandler().EnqueueJson("ok")).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        var expiry = fixture.Expiry();

        // Seven hours in, with eight on the clock: nothing is lapsed, and it is inside the last hour.
        fixture.Clock.Now = Now.AddHours(7).AddMinutes(15);

        Assert.Empty(await expiry.LapsedAsync(TestContext.Current.CancellationToken));

        var soon = await expiry.ExpiringWithinAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var due = Assert.Single(soon);

        Assert.Equal(fixture.SessionId, due.SessionId);
        Assert.Equal(VerificationArtifactState.Expiring, due.State);

        fixture.Clock.Now = Now.AddHours(9);

        var lapsed = Assert.Single(await expiry.LapsedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VerificationArtifactState.Expired, lapsed.State);
    }

    [Fact]
    public async Task TheSweepSettlesLapsedPreviewsAndIsSafeToRunTwice()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        await fixture.Ingestor(Options(), handler: new StubHttpMessageHandler().EnqueueJson("ok")).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        fixture.Clock.Now = Now.AddHours(9);

        var expiry = fixture.Expiry();

        Assert.Equal(1, await expiry.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VerificationArtifactState.Expired, (await fixture.PreviewAsync())?.State);

        // Idempotent: the container can restart and the sweep runs again on the same rows.
        Assert.Equal(0, await expiry.SweepAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TearingDownIsQuietWhenNoProviderCanDoIt()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        // A Render or Coolify self-hoster: the platform's own retention reclaims the environment, and
        // Charter says so rather than pretending it acted.
        var result = await fixture.Expiry().TearDownAsync(fixture.SessionId, TestContext.Current.CancellationToken);

        Assert.False(result.TornDown);
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public async Task AClosedChangeRequestCleansThePreviewUpRatherThanLeavingItReady()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        await fixture.Ingestor(Options(), handler: new StubHttpMessageHandler().EnqueueJson("ok")).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        var listener = new DeploymentChangeRequestListener(
            fixture.Db,
            fixture.Ingestor(Options()),
            fixture.Expiry(),
            NullLogger<DeploymentChangeRequestListener>.Instance);

        await listener.OnDeliveryAsync(
            new GitHubWebhookDelivery
            {
                Type = GitHubWebhookEventType.PullRequest,
                EventName = "pull_request",
                Action = "closed",
                RepositoryFullName = DeploymentScenario.RepoFullName,
                PullRequestNumber = DeploymentScenario.Number,
                PullRequestMerged = true,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationArtifactState.Expired, (await fixture.PreviewAsync())?.State);
    }

    [Fact]
    public async Task AChangeRequestThatMerelyMovedIsLeftAlone()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        await fixture.Ingestor(Options(), handler: new StubHttpMessageHandler().EnqueueJson("ok")).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        var listener = new DeploymentChangeRequestListener(
            fixture.Db,
            fixture.Ingestor(Options()),
            fixture.Expiry(),
            NullLogger<DeploymentChangeRequestListener>.Instance);

        await listener.OnDeliveryAsync(
            new GitHubWebhookDelivery
            {
                Type = GitHubWebhookEventType.PullRequest,
                EventName = "pull_request",
                Action = "synchronize",
                RepositoryFullName = DeploymentScenario.RepoFullName,
                PullRequestNumber = DeploymentScenario.Number,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationArtifactState.Ready, (await fixture.PreviewAsync())?.State);
    }
}
