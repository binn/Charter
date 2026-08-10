using Charter.Data;
using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// The loop that keeps deployments, artifacts and expiry in step (sections 2.3, 18, 27.7).
/// </summary>
/// <remarks>
/// A loop rather than a callback because the container can restart mid-session: a webhook that
/// arrived while the process was rolling has to be picked up from Postgres on the next pass rather
/// than lost. Each test drives one cycle by hand instead of waiting on a timer, and every assertion is
/// about this fixture's own change request — the loop reconciles the whole instance, and the
/// throwaway database is shared with the rest of the suite.
/// </remarks>
public class DeploymentLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private const string PreviewEnvironment =
        "{\"node\":{\"id\":\"env_pr142\",\"name\":\"charter/remember-last-vertical\",\"isEphemeral\":true," +
        "\"meta\":{\"branch\":\"charter/remember-last-vertical\"}}}";

    private const string OtherPreviewEnvironment =
        "{\"node\":{\"id\":\"env_pr7\",\"name\":\"pr-7\",\"isEphemeral\":true,\"meta\":{\"branch\":\"other\"}}}";

    private static DeploymentOptions Options() => new()
    {
        Provider = DeploymentProviderKind.Railway,
        PreviewTtl = TimeSpan.FromHours(8),

        // Reachability has its own tests. Leaving the probe on here would make every assertion depend
        // on how many other change requests the shared database happens to hold.
        ProbeReachability = false,
        Railway = new RailwayOptions
        {
            Token = Charter.Configuration.Secret.From("railway-token")!,
            ProjectId = "proj_123",
            BaseEnvironment = "staging",
            ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
        },
    };

    private static (PreviewLifecycleService Service, RecordingLogger<PreviewLifecycleService> Logger) Build(
        DeploymentFixture fixture,
        DeploymentOptions options,
        RailwayStubHandler railway)
    {
        var provider = new RailwayDeploymentProvider(
            new StubHttpClientFactory(railway),
            options,
            fixture.Clock,
            new RecordingLogger<RailwayDeploymentProvider>());

        var registry = new DeploymentProviderRegistry([provider]);

        var services = new ServiceCollection();
        services.AddSingleton<CharterDbContext>(fixture.Db);
        services.AddSingleton(registry);
        services.AddSingleton(fixture.Ingestor(options, registry));
        services.AddSingleton(fixture.Expiry(registry));

        var container = services.BuildServiceProvider();
        var logger = new RecordingLogger<PreviewLifecycleService>();

        return (
            new PreviewLifecycleService(
                container.GetRequiredService<IServiceScopeFactory>(),
                options,
                fixture.Clock,
                logger),
            logger);
    }

    [Fact]
    public async Task PollingBindsAPreviewNobodyEverToldCharterAbout()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        // Neither the webhook nor a comment ever arrived. Asking Railway directly is the path that is
        // always available, and it is what closes the loop when the other two do not fire.
        var railway = new RailwayStubHandler()
            .WithEnvironments(PreviewEnvironment)
            .WithDeployment(
                "env_pr142",
                "SUCCESS",
                "quote-tool-pr-142.up.railway.app",
                DeploymentScenario.HeadSha);

        var (service, _) = Build(fixture, Options(), railway);

        var cycle = await service.RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.True(cycle.Polled >= 1);

        var artifact = await fixture.PreviewAsync();

        Assert.Equal(VerificationArtifactState.Ready, artifact?.State);
        Assert.Equal("https://quote-tool-pr-142.up.railway.app/", artifact?.Url);
        Assert.Equal(Now.AddHours(8), artifact?.ExpiresAt);
    }

    [Fact]
    public async Task APreviewThatIsNeverComingIsSaidOutLoudRatherThanWaitedOnForever()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        // Railway has an environment for another change request and none for this one, long after the
        // commit was pushed: section 18's outside-the-workspace case, which reports no error anywhere.
        var railway = new RailwayStubHandler().WithEnvironments(OtherPreviewEnvironment);

        var (service, logger) = Build(fixture, Options(), railway);

        fixture.Clock.Now = Now.AddHours(2);

        var cycle = await service.RunOnceAsync(fixture.Clock.Now, TestContext.Current.CancellationToken);

        Assert.True(cycle.Blocked >= 1);

        var warning = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                     && entry.Message.Contains(DeploymentScenario.RepoFullName, StringComparison.Ordinal));

        Assert.Contains("invited", warning.Message, StringComparison.Ordinal);
        Assert.Contains("142", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACycleAlsoSettlesWhateverHasLapsedAndStopsPayingForIt()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        var options = Options();

        await fixture.Ingestor(options).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationArtifactState.Ready, (await fixture.PreviewAsync())?.State);

        fixture.Clock.Now = Now.AddHours(9);

        var railway = new RailwayStubHandler()
            .WithEnvironments(PreviewEnvironment)
            .WithDeployment(
                "env_pr142",
                "SUCCESS",
                "quote-tool-pr-142.up.railway.app",
                DeploymentScenario.HeadSha);

        var (service, _) = Build(fixture, options, railway);

        var cycle = await service.RunOnceAsync(fixture.Clock.Now, TestContext.Current.CancellationToken);

        Assert.True(cycle.Expired >= 1);
        Assert.Equal(VerificationArtifactState.Expired, (await fixture.PreviewAsync())?.State);

        // Section 27.5: without this, hosting and storage costs run away.
        Assert.Contains(
            railway.RequestBodies,
            body => body.Contains("environmentDelete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadyPreviewIsReconciledWithoutWritingAnything()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(Now);
        if (fixture is null)
        {
            return;
        }

        var options = Options();

        await fixture.Ingestor(options).ReportAsync(
            DeploymentScenario.HeadSha,
            new DeploymentWebhookRequest("https://quote-tool-pr-142.up.railway.app", "ready", "railway"),
            TestContext.Current.CancellationToken);

        var railway = new RailwayStubHandler().WithEnvironments(PreviewEnvironment);

        var (service, _) = Build(fixture, options, railway);

        var cycle = await service.RunOnceAsync(Now, TestContext.Current.CancellationToken);

        // A loop that rewrote a row every fifteen seconds would be a loop nobody could leave running.
        Assert.Equal(0, cycle.Reconciled);
        Assert.Equal(VerificationArtifactState.Ready, (await fixture.PreviewAsync())?.State);
    }
}
