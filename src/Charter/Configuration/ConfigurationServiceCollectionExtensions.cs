using Charter.Configuration.Preflight;
using Microsoft.Extensions.DependencyInjection;

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
    /// Registers the section 30.1 preflight checks and the runner that reports them.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="AddCharterConfig"/> to have run. The checks that need I/O resolve
    /// <see cref="IDatabaseProbe"/> and <see cref="IHostnameResolver"/>, both replaceable, so the
    /// first-run report can be exercised in a test without a live Postgres or a DNS server.
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

        return services;
    }
}
