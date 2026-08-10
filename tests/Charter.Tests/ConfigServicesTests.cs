using Charter.Configuration;
using Charter.Configuration.Preflight;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Section 4.1: the parsed config is registered as a singleton. Sections are registered too, so a
/// consumer can depend on the part it needs rather than the whole of section 4.2.
/// </summary>
public class ConfigServicesTests
{
    [Fact]
    public void RegistersTheConfigAndItsSectionsAsSingletons()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_SMTP_URL", "smtp://smtp.example.com"),
            ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
            ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
            ("CHARTER_STORAGE_ACCESS_KEY", "access"),
            ("CHARTER_STORAGE_SECRET_KEY", "secret"));

        using var provider = new ServiceCollection().AddCharterConfig(config).BuildServiceProvider();

        Assert.Same(config, provider.GetRequiredService<CharterConfig>());
        Assert.Same(config, provider.GetRequiredService<CharterConfig>());
        Assert.Same(config.Database, provider.GetRequiredService<DatabaseConfig>());
        Assert.Same(config.Keys, provider.GetRequiredService<KeyConfig>());
        Assert.Same(config.Models, provider.GetRequiredService<ModelConfig>());
        Assert.Same(config.GitHub, provider.GetRequiredService<GitHubConfig>());
        Assert.Same(config.Logging, provider.GetRequiredService<LoggingConfig>());
        Assert.Same(config.Telemetry, provider.GetRequiredService<TelemetryConfig>());
        Assert.Same(config.Auth, provider.GetRequiredService<AuthConfig>());
        Assert.Same(config.Budgets, provider.GetRequiredService<BudgetConfig>());
        Assert.Same(config.UpdateCheck, provider.GetRequiredService<UpdateCheckConfig>());
        Assert.Same(config.Smtp, provider.GetRequiredService<SmtpConfig>());
        Assert.Same(config.Storage, provider.GetRequiredService<StorageConfig>());
    }

    [Fact]
    public void LeavesOptionalSectionsUnregisteredWhenTheyAreNotConfigured()
    {
        var config = ConfigTestEnvironment.Valid();

        using var provider = new ServiceCollection().AddCharterConfig(config).BuildServiceProvider();

        Assert.Null(provider.GetService<StorageConfig>());
        Assert.Null(provider.GetService<SmtpConfig>());
    }

    [Fact]
    public void RegistersEveryPreflightCheckSection301AsksFor()
    {
        var config = ConfigTestEnvironment.Valid();

        using var provider = new ServiceCollection()
            .AddCharterConfig(config)
            .AddCharterPreflight()
            .BuildServiceProvider();

        var checks = provider.GetServices<IPreflightCheck>().ToList();

        Assert.Equal(
            ["secret keys", "base URL", "database", "migrations", "model credential"],
            checks.Select(check => check.Name).ToArray());
        Assert.NotNull(provider.GetRequiredService<PreflightRunner>());
        Assert.IsType<SystemHostnameResolver>(provider.GetRequiredService<IHostnameResolver>());
        Assert.IsType<NpgsqlDatabaseProbe>(provider.GetRequiredService<IDatabaseProbe>());
    }
}
