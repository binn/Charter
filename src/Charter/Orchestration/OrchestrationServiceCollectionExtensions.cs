using Charter;
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
                options.BuildModel = config.Models.Build.ToString();

                // CHARTER_RUNNER is a list in preference order (section 2.2), so the first entry is
                // what a new session is queued against. Routing still decides at dispatch time.
                if (config.Runners.Count > 0)
                {
                    options.DefaultRunner = config.Runners[0] switch
                    {
                        RunnerBackend.GitHubActions => Charter.Domain.RunnerKind.GitHubActions,
                        RunnerBackend.Docker => Charter.Domain.RunnerKind.Docker,
                        _ => Charter.Domain.RunnerKind.Agent,
                    };
                }
            }

            configure?.Invoke(options);
            return options;
        });

        services.TryAddSingleton(CharterTime.System);

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

        // Section 11's pane 1. Registered here rather than beside a backend because every backend's
        // events arrive through the same callback, and a requester must never watch a silent thread
        // for the length of a build whichever runner is doing the work.
        services.AddScoped<SessionMilestones>();

        // Section 6's first notifying state. Registered here rather than in AddCharterRunners because
        // the runner callbacks are mapped whatever CHARTER_RUNNER says, and an agent that stops to ask
        // a question must reach somebody on every backend. INotificationService is optional on it, so
        // a host that wired no channels still transitions the session and simply tells nobody.
        services.AddScoped<NeedsInputAnnouncer>();


        services.TryAddScoped<IAutoDispatchGate, AutoDispatchGate>();
        services.AddScoped<SessionCoordinator>();

        // One handler per job type, resolved by the dispatcher. A type with no handler is deferred
        // rather than completed, so a missing registration here shows up as work that never runs
        // rather than as work that quietly disappears.
        services.AddScoped<IQueuedJobHandler, BuildJobHandler>();
        services.AddScoped<IQueuedJobHandler, RefineJobHandler>();
        services.AddScoped<IQueuedJobHandler, RecapJobHandler>();

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
            // The tokens are resolved lazily rather than required, as for DockerRunner below:
            // AddCharterRunners is called before AddCharterOrchestration registers them. A dispatch
            // without them refuses with the variable to set rather than sending a payload the
            // credential exchange would then reject.
            services.AddSingleton<IAgentRunner>(provider => new GitHubActionsRunner(
                provider.GetRequiredService<IGitHubRepositoryDispatcher>(),
                provider.GetRequiredService<GitHubActionsRunnerOptions>(),
                provider.GetRequiredService<ILogger<GitHubActionsRunner>>(),
                provider.GetService<RunnerSessionTokens>()));
        }

        // The primary backend (section 2.2). It registers its own pairing service, connection
        // registry and credential mint, because those are reachable over HTTP whether or not the
        // dispatcher ever routes to an agent — an operator has to be able to pair one first.
        if (config.SupportsRunner(RunnerBackend.Agent))
        {
            services.AddCharterAgentPlane();
        }

        // The Compose self-host backend (section 2.2). Registered behind the same seam as the other
        // two, and never silently: an instance whose CHARTER_RUNNER says `docker` now has a runner
        // that either dispatches or explains itself, rather than a queue that never moves.
        if (config.SupportsRunner(RunnerBackend.Docker))
        {
            services.TryAddSingleton(new DockerRunnerOptions
            {
                SocketPath = DockerRunnerEnvironment.SocketPath(),
            });

            services.TryAddSingleton(provider => DockerRunnerEnvironment.Resolve(
                provider.GetRequiredService<DockerRunnerOptions>().SocketPath));

            // The tokens and the broker are resolved lazily rather than required: AddCharterRunners is
            // called before AddCharterOrchestration registers them, and a test that wires only a
            // backend should not have to bring a signing key with it.
            services.AddSingleton<IAgentRunner>(provider => new DockerRunner(
                provider.GetRequiredService<IDockerEngine>(),
                provider.GetRequiredService<DockerRunnerOptions>(),
                provider.GetRequiredService<ILogger<DockerRunner>>(),
                provider.GetService<RunnerSessionTokens>(),
                provider.GetService<IRunnerCredentialBroker>()));
        }

        return services;
    }
}
