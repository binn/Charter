using Charter.Configuration;
using Charter.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// Registers preview environment binding (section 18) and the verification artifact it produces
/// (section 27.1).
/// </summary>
/// <remarks>
/// <para>Host wiring (see <c>Program.cs</c>):</para>
/// <code>
/// builder.Services.AddCharterDeployments();   // after AddCharterGitHub() and AddCharterData()
/// </code>
/// <para>
/// There is no <c>MapCharterDeployments</c>: section 18's endpoint,
/// <c>POST /api/deployments/{prSha}</c>, already exists and is mapped by <c>MapCharterGitHub()</c>.
/// Adding a second route for the same thing would give self-hosters two answers to the question of
/// where to point their platform.
/// </para>
/// <para>
/// Registering this with no provider configured is a supported, first-class configuration — it is
/// what a Render, Fly or Coolify self-hoster runs. The webhook still binds previews, the artifact is
/// still produced, and expiry still works; only polling, comment parsing and teardown are absent, and
/// each of those checks its provider before assuming it has one.
/// </para>
/// </remarks>
public static class DeploymentsServiceCollectionExtensions
{
    /// <summary>Adds preview binding, reading section 18's configuration from the environment.</summary>
    public static IServiceCollection AddCharterDeployments(this IServiceCollection services)
        => services.AddCharterDeployments(DeploymentOptions.FromEnvironment());

    /// <summary>Adds preview binding with explicit options.</summary>
    /// <exception cref="ConfigException">
    /// A provider is selected but not fully configured. Section 4.1: validate at startup and fail
    /// loudly, rather than discovering it at the moment a requester is waiting for a preview.
    /// </exception>
    public static IServiceCollection AddCharterDeployments(
        this IServiceCollection services,
        DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Errors.Count > 0)
        {
            throw new ConfigException([.. options.Errors.Select(problem => problem.Text)]);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IPreviewHostResolver, DnsPreviewHostResolver>();
        services.TryAddSingleton<PreviewUrlPolicy>();

        // The probe's client, and the only outbound request Charter makes to an address somebody else
        // chose. The handler resolves and checks every address before it opens a socket to it, and
        // does not follow redirects — a redirect is a second URL nothing has checked.
        services
            .AddHttpClient(
                PreviewReachabilityProbe.HttpClientName,
                client => PreviewHttpClient.Configure(client, options.ProbeTimeout))
            .ConfigurePrimaryHttpMessageHandler(provider =>
                PreviewHttpClient.CreateHandler(provider.GetRequiredService<PreviewUrlPolicy>()));

        services.TryAddSingleton<PreviewReachabilityProbe>();
        services.TryAddSingleton<DeploymentProviderRegistry>();

        // Scoped: these read and write rows through the request's CharterDbContext. The binder is
        // section 18's existing one, deliberately reused rather than reimplemented.
        services.TryAddScoped<DeploymentBinder>();
        services.TryAddScoped<PreviewReadyAnnouncer>();
        services.TryAddScoped<PreviewArtifactPublisher>();
        services.TryAddScoped<PreviewExpiry>();
        services.TryAddScoped<DeploymentIngestor>();

        if (options.Provider == DeploymentProviderKind.Railway)
        {
            services.AddHttpClient(RailwayDeploymentProvider.HttpClientName);
            services.AddSingleton<IDeploymentProvider, RailwayDeploymentProvider>();
        }

        services.AddScoped<IGitHubWebhookListener, DeploymentChangeRequestListener>();

        // Section 18's comment fallback. Registered unconditionally rather than only when a provider
        // that parses comments is configured: the ingestor already refuses when there is none, and
        // one refusal path beats a listener whose presence depends on configuration read at startup.
        services.AddScoped<IGitHubWebhookListener, DeploymentCommentListener>();
        services.AddHostedService<PreviewLifecycleService>();
        services.AddHostedService<DeploymentStartupWarnings>();

        return services;
    }
}

/// <summary>
/// Says out loud, once, everything questionable about the deployment configuration.
/// </summary>
/// <remarks>
/// Section 18's requirement that previews be based off a staging environment rather than production
/// is a security property, not a preference: a Railway PR environment replicates every service,
/// database and variable from its base, so a production base hands real credentials to an environment
/// anybody who can open a change request can reach. Configuration parsing records that as a warning;
/// this is what makes it loud, at the top of the log where an operator looks, rather than a footnote
/// nobody reads.
/// </remarks>
public sealed class DeploymentStartupWarnings : IHostedService
{
    private readonly DeploymentOptions _options;
    private readonly ILogger<DeploymentStartupWarnings> _logger;

    public DeploymentStartupWarnings(DeploymentOptions options, ILogger<DeploymentStartupWarnings> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in _options.Warnings)
        {
            _logger.LogWarning("Deployment configuration: {Problem}", warning.Text);
        }

        if (_options.Provider == DeploymentProviderKind.None)
        {
            _logger.LogInformation(
                "No deployment provider is configured. Previews bind through POST /api/deployments/{{prSha}} " +
                "(section 18); Charter will not poll a platform, read its comments, or tear its " +
                "environments down.");
        }

        // Not a configuration error — an instance that binds previews from Railway's comments never
        // needs this — but an instance whose platform posts to the webhook gets nothing at all without
        // it, and silence would send the operator looking at their platform instead.
        if (_options.WebhookSecret is null)
        {
            _logger.LogWarning(
                "CHARTER_DEPLOYMENT_WEBHOOK_SECRET is not set, so POST /api/deployments/{{prSha}} refuses " +
                "every report. Set it, and send it as an Authorization: Bearer header, an {Header} " +
                "header, or a ?{Query}= parameter from your platform's post-deploy hook.",
                DeploymentWebhookAuthentication.HeaderName,
                DeploymentWebhookAuthentication.QueryName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
