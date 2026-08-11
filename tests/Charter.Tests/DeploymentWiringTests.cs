using Charter.Configuration;
using Charter.Data;
using Charter.Deployments;
using Charter.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// What <c>AddCharterDeployments</c> puts in the container, and what it refuses to.
/// </summary>
public class DeploymentWiringTests
{
    private static ServiceProvider Build(DeploymentOptions options)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCharterData("Host=localhost;Database=charter;Username=charter;Password=charter");
        services.AddCharterDeployments(options);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AnInstanceWithNoProviderStillGetsBothIngestionPathsAndExpiry()
    {
        using var provider = Build(DeploymentOptions.WebhookOnly);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DeploymentIngestor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PreviewArtifactPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PreviewExpiry>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DeploymentBinder>());

        var registry = scope.ServiceProvider.GetRequiredService<DeploymentProviderRegistry>();

        Assert.Empty(registry.All);
        Assert.Null(registry.Configured);
    }

    [Fact]
    public void RailwayIsRegisteredOnlyWhenItIsTheConfiguredProvider()
    {
        var options = DeploymentOptions.Parse(name => name switch
        {
            "CHARTER_DEPLOYMENT_PROVIDER" => "railway",
            "CHARTER_RAILWAY_TOKEN" => "railway-token",
            "CHARTER_RAILWAY_PROJECT_ID" => "proj_123",
            "CHARTER_RAILWAY_BASE_ENVIRONMENT" => "staging",
            _ => null,
        });

        using var provider = Build(options);
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<DeploymentProviderRegistry>();
        var railway = Assert.IsType<RailwayDeploymentProvider>(registry.Configured);

        Assert.Equal("railway", railway.Id);
        Assert.Equal("staging", railway.BaseEnvironment);
    }

    [Fact]
    public void AHalfConfiguredProviderStopsStartupRatherThanFailingAtTheWorstMoment()
    {
        var options = DeploymentOptions.Parse(name =>
            name == "CHARTER_DEPLOYMENT_PROVIDER" ? "railway" : null);

        var services = new ServiceCollection();

        var thrown = Assert.Throws<ConfigException>(() => services.AddCharterDeployments(options));

        Assert.Contains(thrown.Problems, problem => problem.Contains("CHARTER_RAILWAY_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLifecycleLoopAndTheStartupWarningsAreHostedServices()
    {
        using var provider = Build(DeploymentOptions.WebhookOnly);

        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, service => service is PreviewLifecycleService);
        Assert.Contains(hosted, service => service is DeploymentStartupWarnings);
    }

    [Fact]
    public void TheChangeRequestListenerJoinsTheWebhookFanOut()
    {
        using var provider = Build(DeploymentOptions.WebhookOnly);
        using var scope = provider.CreateScope();

        var listeners = scope.ServiceProvider.GetServices<IGitHubWebhookListener>().ToList();

        Assert.Contains(listeners, listener => listener is DeploymentChangeRequestListener);

        // Section 18's comment fallback rides the same fan-out, and is registered whether or not a
        // provider that reads comments is configured — the ingestor is what refuses.
        Assert.Contains(listeners, listener => listener is DeploymentCommentListener);
    }

    [Fact]
    public void TheGenericWebhookRouteIsTheOneSectionEighteenDocuments()
    {
        // There is deliberately no second route for the same thing: self-hosters get one answer to
        // the question of where to point their platform.
        Assert.Equal("/api/deployments/{prSha}", GitHubWebhookEndpoints.DeploymentPath);
    }

    [Fact]
    public async Task AnInstanceWithNoProviderSaysSoOnceRatherThanLookingBroken()
    {
        var logger = new RecordingLogger<DeploymentStartupWarnings>();
        var warnings = new DeploymentStartupWarnings(DeploymentOptions.WebhookOnly, logger);

        await warnings.StartAsync(TestContext.Current.CancellationToken);

        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);

        Assert.Contains("/api/deployments/", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstanceWithNoDeploymentSecretSaysSoRatherThanSilentlyRefusingEveryReport()
    {
        var logger = new RecordingLogger<DeploymentStartupWarnings>();
        var warnings = new DeploymentStartupWarnings(DeploymentOptions.WebhookOnly, logger);

        await warnings.StartAsync(TestContext.Current.CancellationToken);

        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("CHARTER_DEPLOYMENT_WEBHOOK_SECRET", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewProbeGetsTheGuardedHandlerRatherThanABareOne()
    {
        // Asserting on PreviewHttpClient.CreateHandler proves nothing unless the client the probe
        // actually resolves is built from it. This is the wiring that makes the guard real: the
        // control plane's one outbound request to an address somebody else chose.
        using var provider = Build(DeploymentOptions.WebhookOnly);

        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        using var chain = factory.CreateHandler(PreviewReachabilityProbe.HttpClientName);

        var handler = chain;
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            handler = inner;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);

        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseProxy);
        Assert.NotNull(sockets.ConnectCallback);
    }
}
