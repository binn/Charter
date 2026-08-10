using Charter.Runners;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.GitHub;

/// <summary>
/// Registers the GitHub App integration: authentication, repository access and the webhooks.
/// </summary>
/// <remarks>
/// <para>Host wiring (see <c>Program.cs</c>):</para>
/// <code>
/// builder.Services.AddCharterGitHub();      // after AddCharterConfig() and AddCharterData()
/// // ...
/// app.MapCharterGitHub();                   // after UseAuthorization(), before MapFallbackToFile
/// </code>
/// <para>
/// Requires <c>AddCharterConfig</c> (for <see cref="Charter.Configuration.GitHubConfig"/>) and
/// <c>AddCharterData</c> (for <c>CharterDbContext</c>). Nothing here reads the environment itself.
/// </para>
/// </remarks>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>Adds the GitHub App integration with default options.</summary>
    public static IServiceCollection AddCharterGitHub(this IServiceCollection services)
        => services.AddCharterGitHub(new GitHubOptions());

    /// <summary>Adds the GitHub App integration with explicit options.</summary>
    public static IServiceCollection AddCharterGitHub(this IServiceCollection services, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);

        services.AddHttpClient(GitHubAppTokenProvider.HttpClientName);

        // Singleton so the installation-token cache is shared. A scoped provider would mint a fresh
        // token per request, which is both slower and noisier in GitHub's own audit log.
        services.TryAddSingleton<IGitHubAppTokenProvider, GitHubAppTokenProvider>();
        services.TryAddSingleton<IGitHubRunnerCredentialFactory, GitHubRunnerCredentialFactory>();
        services.TryAddSingleton<IGitHubRepositoryClient, GitHubRepositoryClient>();

        services.TryAddSingleton<GitHubWebhookReceiver>();

        // Scoped: reads pull request rows through the request's CharterDbContext.
        services.TryAddScoped<DeploymentBinder>();

        // Singleton, and the same instance behind both seams: the runners that consume them are
        // singletons, so this takes its CharterDbContext from a scope it creates per call rather
        // than capturing one. Registered ahead of AddCharterOrchestration's "unconfigured" fallbacks
        // — whichever runs first wins, and both sides use TryAdd, so the wiring order in Program.cs
        // is what decides. Put AddCharterGitHub() first.
        services.TryAddSingleton<GitHubRepositoryDispatcher>();
        services.TryAddSingleton<IGitHubRepositoryDispatcher>(provider =>
            provider.GetRequiredService<GitHubRepositoryDispatcher>());
        services.TryAddSingleton<IRunnerCredentialBroker>(provider =>
            provider.GetRequiredService<GitHubRepositoryDispatcher>());

        return services;
    }
}
