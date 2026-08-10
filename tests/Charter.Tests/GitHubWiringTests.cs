using Charter.Auth;
using Charter.Configuration;
using Charter.Data;
using Charter.GitHub;
using Charter.Onboarding;
using Charter.Runners;
using Charter.VersionControl;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterGitHub</c> and <c>AddCharterOnboarding</c> register graphs that resolve.
/// </summary>
/// <remarks>
/// A registration mistake — a singleton capturing a scoped <c>DbContext</c>, most likely — only shows
/// up on the first webhook after a deploy. Resolving everything here moves that discovery to the
/// build.
/// </remarks>
public class GitHubWiringTests
{
    [Fact]
    public void EveryGitHubServiceResolves()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<IGitHubAppTokenProvider>());
        Assert.NotNull(services.GetRequiredService<IGitHubRunnerCredentialFactory>());
        Assert.NotNull(services.GetRequiredService<IGitHubRepositoryClient>());
        Assert.NotNull(services.GetRequiredService<GitHubWebhookReceiver>());
        Assert.NotNull(services.GetRequiredService<DeploymentBinder>());
        Assert.NotNull(services.GetRequiredService<GitHubOptions>());
    }

    [Fact]
    public void EveryOnboardingServiceResolves()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<ICharterFolderLoader>());
        Assert.NotNull(services.GetRequiredService<CharterFolderCache>());
        Assert.NotNull(services.GetRequiredService<OnboardingService>());
        Assert.NotNull(services.GetRequiredService<IOnboardingRunDispatcher>());
        Assert.NotNull(services.GetRequiredService<RequestableRepoQuery>());
    }

    [Fact]
    public void TheCallbacksSeamIsTheSameInstanceAsTheService()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();

        // The execution plane depends on four methods rather than the whole service, but it must be
        // talking to the same object — two instances would mean two half-written transitions.
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<OnboardingService>(),
            scope.ServiceProvider.GetRequiredService<IOnboardingRunCallbacks>());
    }

    [Fact]
    public void TheExecutionPlanesGitHubSeamsAreFilledByTheGitHubClient()
    {
        using var app = Build();

        // Both are declared in Charter.Runners and both say they are implemented over the GitHub App.
        // Resolving them from the root provider is the real test: the runners that consume them are
        // singletons, so a scoped implementation would be a captive dependency.
        var dispatcher = app.Services.GetRequiredService<IGitHubRepositoryDispatcher>();
        var broker = app.Services.GetRequiredService<IRunnerCredentialBroker>();

        Assert.IsType<GitHubRepositoryDispatcher>(dispatcher);
        Assert.IsType<GitHubRepositoryDispatcher>(broker);
        Assert.Same(dispatcher, broker);
    }

    [Fact]
    public void TheOnboardingListenerIsRegisteredAsOneOfSeveral()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();

        // Enumerable, not TryAdd: onboarding is one listener among however many subscribe, and the
        // receiver fans out to all of them.
        Assert.Contains(
            scope.ServiceProvider.GetServices<IGitHubWebhookListener>(),
            listener => listener is OnboardingWebhookListener);
    }

    [Fact]
    public void TheVersionControlSeamIsFilledByGitHubAndOnlyByGitHub()
    {
        using var app = Build();

        // Change spec 001 part A: the interface exists from Phase 1 and ships exactly one
        // implementation. A second provider appearing here would mean somebody shipped one early.
        var registry = app.Services.GetRequiredService<IVersionControlProviderRegistry>();

        var provider = Assert.Single(registry.Providers);
        Assert.IsType<GitHubVersionControlProvider>(provider);
        Assert.Equal("github", provider.Id);
        Assert.Equal("pull request", provider.Terms.ChangeRequest);
    }

    [Fact]
    public void TheChangeRequestListenerIsOneOfTheWebhookListeners()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();

        // Section 17 and section 6 both need change request state, and it arrives on the webhook
        // Charter already receives rather than on a second endpoint.
        Assert.Contains(
            scope.ServiceProvider.GetServices<IGitHubWebhookListener>(),
            listener => listener is GitHubChangeRequestListener);
    }

    [Fact]
    public void TheChangeRequestServicesResolve()
    {
        using var app = Build();
        using var scope = app.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChangeRequestPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChangeRequestStateTracker>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MergeGateInspector>());
    }

    [Fact]
    public void CallingBothRegistrationsTwiceIsHarmless()
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();
        builder.Services.AddCharterGitHub();
        builder.Services.AddCharterOnboarding();
        builder.Services.AddCharterGitHub();

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGitHubRepositoryClient>());
        Assert.Single(
            scope.ServiceProvider.GetServices<IGitHubWebhookListener>(),
            listener => listener is OnboardingWebhookListener);

        // Two providers answering identically would mean every capability check had a coin flip in it.
        Assert.Single(app.Services.GetRequiredService<IVersionControlProviderRegistry>().Providers);
    }

    [Fact]
    public void TheWebhookRoutesAreWhereTheGitHubAppAndSection18Expect()
    {
        Assert.Equal("/api/github/webhook", GitHubWebhookEndpoints.WebhookPath);
        Assert.Equal("/api/deployments/{prSha}", GitHubWebhookEndpoints.DeploymentPath);
    }

    private static WebApplication Build()
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();
        builder.Services.AddCharterGitHub();
        builder.Services.AddCharterOnboarding();

        return builder.Build();
    }
}
