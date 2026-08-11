using Charter.Configuration.Preflight;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Charter.Configuration;

/// <summary>
/// Registers the validated configuration and the first-run preflight checks.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <paramref name="config"/> as a singleton, along with each of its sections so a
    /// consumer can depend on the part it needs rather than the whole of section 4.2.
    /// </summary>
    /// <remarks>
    /// Section 4.1: config is parsed once at startup into an immutable record registered as a
    /// singleton. Parsing happens before the host is built - see
    /// <see cref="CharterConfigParser.Parse()"/> - so this method takes an already-validated instance
    /// and never reads the environment itself.
    /// </remarks>
    public static IServiceCollection AddCharterConfig(this IServiceCollection services, CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(config);
        services.AddSingleton(config.Database);
        services.AddSingleton(config.Keys);
        services.AddSingleton(config.Models);
        services.AddSingleton(config.GitHub);
        services.AddSingleton(config.Logging);
        services.AddSingleton(config.Telemetry);
        services.AddSingleton(config.Auth);
        services.AddSingleton(config.Budgets);
        services.AddSingleton(config.UpdateCheck);
        services.AddSingleton(config.Email);

        // Optional sections are registered only when configured, so an injected StorageConfig? is
        // the honest signal that object storage is off rather than a stub that fails on first use.
        if (config.Storage is not null)
        {
            services.AddSingleton(config.Storage);
        }

        if (config.Smtp is not null)
        {
            services.AddSingleton(config.Smtp);
        }

        return services;
    }

    /// <summary>
    /// Registers the section 30.1 preflight checks, the runner that reports them, and the startup
    /// hook that actually runs them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <see cref="AddCharterConfig"/> to have run. The checks that need I/O resolve
    /// <see cref="IDatabaseProbe"/> and <see cref="IHostnameResolver"/>, both replaceable, so the
    /// first-run report can be exercised in a test without a live Postgres or a DNS server.
    /// </para>
    /// <para>
    /// <see cref="PreflightHostedService"/> is what makes this more than a library. For a while the
    /// checks were registered, built and unit-tested while nothing in the running application ever
    /// asked them anything - the same defect class as a configuration variable that parses and is
    /// then ignored, and worse, because the operator believed the instance had been checked.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterPreflight(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostnameResolver, SystemHostnameResolver>();
        services.AddSingleton<IDatabaseProbe>(provider =>
            new NpgsqlDatabaseProbe(provider.GetRequiredService<CharterConfig>()));

        services.AddSingleton<IPreflightCheck>(provider =>
            new KeyStrengthPreflightCheck(provider.GetRequiredService<CharterConfig>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new OutboundCallsPreflightCheck(provider.GetRequiredService<CharterConfig>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new GitHubAppPreflightCheck(provider.GetRequiredService<CharterConfig>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new BaseUrlPreflightCheck(
                provider.GetRequiredService<CharterConfig>(),
                provider.GetRequiredService<IHostnameResolver>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new DatabaseConnectivityPreflightCheck(
                provider.GetRequiredService<CharterConfig>(),
                provider.GetRequiredService<IDatabaseProbe>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new MigrationsPreflightCheck(provider.GetRequiredService<IDatabaseProbe>()));
        services.AddSingleton<IPreflightCheck>(provider =>
            new ModelCredentialPreflightCheck(
                provider.GetRequiredService<CharterConfig>(),
                provider.GetRequiredService<IDatabaseProbe>()));

        services.AddSingleton(provider =>
            new PreflightRunner(provider.GetServices<IPreflightCheck>()));

        // Inserted at the front rather than appended. Hosted services start in registration order,
        // and the ASP.NET Core server is itself one of them, registered while the WebApplicationBuilder
        // is constructed - which is before this method runs. Appending would let Kestrel bind a socket
        // and serve requests before preflight had decided whether the instance works, which is the
        // half-working boot section 30.1 forbids.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, PreflightHostedService>());

        return services;
    }
}
