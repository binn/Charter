using Charter.Configuration;
using Charter.Orchestration;
using Charter.Runners;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the execution plane: the runner backends, the session orchestrator, and the queue
/// dispatcher (sections 2.1, 2.3, 23).
/// </summary>
/// <remarks>
/// Placed in <c>Microsoft.Extensions.DependencyInjection</c> so <c>AddCharterOrchestration()</c>
/// resolves from <c>Program.cs</c> with no extra using directive, matching every other <c>Add*</c>
/// in that file.
/// </remarks>
public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the execution plane. Requires <c>AddCharterData</c> and, for a runner that talks to
    /// GitHub, <c>AddCharterConfig</c>.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">
    /// Overrides for <see cref="OrchestrationOptions"/>. Runs after the defaults have been projected
    /// from <see cref="CharterConfig"/>, so a caller can change anything the environment implied.
    /// </param>
    /// <remarks>
    /// <para>
    /// Which backends are registered follows <c>CHARTER_RUNNER</c> (section 2.2). Several may be
    /// enabled at once and the dispatcher routes between them by capability match (section 27.3), so
    /// this is an additive list rather than a switch.
    /// </para>
    /// <para>
    /// The two hosted services are the whole of the control plane's half of section 2.1. Neither
    /// holds session state; both re-read Postgres every cycle, which is what makes a container
    /// restart mid-session a non-event (section 2.3).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterOrchestration(
        this IServiceCollection services,
        Action<OrchestrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider =>
        {
            var options = new OrchestrationOptions();

            if (provider.GetService<CharterConfig>() is { } config)
            {
                options.BaseUrl = config.BaseUrl;
            }

            configure?.Invoke(options);
            return options;
        });

        // The seam. Backends only ever dispatch; the work itself is Charter.DetachedRunner's.
        services.TryAddSingleton<IGitHubRepositoryDispatcher, UnconfiguredGitHubRepositoryDispatcher>();
        services.TryAddSingleton<IRunnerCredentialBroker, UnconfiguredRunnerCredentialBroker>();
        services.TryAddSingleton(new GitHubActionsRunnerOptions());

        services.AddSingleton<IRunnerRegistry>(provider => new RunnerRegistry(
            provider.GetServices<IAgentRunner>()));

        services.AddSingleton(provider => provider.GetService<KeyConfig>() is { } keys
            ? new RunnerSessionTokens(keys)
            : throw new InvalidOperationException(
                "AddCharterOrchestration needs CHARTER_SECRET_KEY. Call AddCharterConfig first."));

        services.AddScoped<SessionJournal>();
        services.AddScoped<ISessionDispatchPlanner, SessionDispatchPlanner>();
        services.AddScoped<SessionCoordinator>();
        services.AddScoped<IQueuedJobHandler, BuildJobHandler>();

        services.AddHostedService<SessionOrchestrator>();
        services.AddHostedService<QueueDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers the backends named by <c>CHARTER_RUNNER</c> (section 4.2).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddCharterOrchestration"/> so a test — or a self-hoster wiring a
    /// backend Charter does not ship — can register its own <see cref="IAgentRunner"/> instead of the
    /// configured set without having to opt out of the orchestrator.
    /// </remarks>
    public static IServiceCollection AddCharterRunners(this IServiceCollection services, CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        if (config.SupportsRunner(RunnerBackend.GitHubActions))
        {
            services.AddSingleton<IAgentRunner>(provider => new GitHubActionsRunner(
                provider.GetRequiredService<IGitHubRepositoryDispatcher>(),
                provider.GetRequiredService<GitHubActionsRunnerOptions>(),
                provider.GetRequiredService<ILogger<GitHubActionsRunner>>()));
        }

        // `agent` and `docker` register their backends from Charter.Agent's control-plane half and
        // from the Docker runner respectively; both are section 2.2 backends behind the same seam.
        return services;
    }
}
