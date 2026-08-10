using Charter.Configuration;
using Charter.Deployments;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// Section 18's configuration, and the one part of it that is a security property rather than a
/// preference.
/// </summary>
public class DeploymentConfigTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var values = pairs
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

        return name => values.TryGetValue(name, out var value) ? value : null;
    }

    private static Func<string, string?> Railway(string baseEnvironment)
        => Env(
            ("CHARTER_DEPLOYMENT_PROVIDER", "railway"),
            ("CHARTER_RAILWAY_TOKEN", "railway-token"),
            ("CHARTER_RAILWAY_PROJECT_ID", "proj_123"),
            ("CHARTER_RAILWAY_BASE_ENVIRONMENT", baseEnvironment));

    [Fact]
    public void NoProviderIsTheDefaultAndIsAValidConfiguration()
    {
        // A Render, Fly or Coolify self-hoster runs exactly this and binds previews through the
        // generic webhook. It is first-class, not a broken instance.
        var options = DeploymentOptions.Parse(Env());

        Assert.Equal(DeploymentProviderKind.None, options.Provider);
        Assert.Empty(options.Errors);
        Assert.Null(options.Railway);
    }

    [Fact]
    public void RailwayNeedsATokenAProjectAndABaseEnvironment()
    {
        var options = DeploymentOptions.Parse(Env(("CHARTER_DEPLOYMENT_PROVIDER", "railway")));

        var named = options.Errors.Select(problem => problem.Variable).ToList();

        Assert.Contains("CHARTER_RAILWAY_TOKEN", named);
        Assert.Contains("CHARTER_RAILWAY_PROJECT_ID", named);
        Assert.Contains("CHARTER_RAILWAY_BASE_ENVIRONMENT", named);
        Assert.Null(options.Railway);
    }

    [Fact]
    public void AStagingBaseEnvironmentPassesWithoutComment()
    {
        var options = DeploymentOptions.Parse(Railway("staging"));

        Assert.Equal(DeploymentProviderKind.Railway, options.Provider);
        Assert.NotNull(options.Railway);
        Assert.Equal("staging", options.Railway.BaseEnvironment);
        Assert.False(options.Railway.BaseLooksLikeProduction);
        Assert.Empty(options.Errors);
        Assert.Empty(options.Warnings);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("Production")]
    [InlineData("prod")]
    [InlineData("prod-eu")]
    [InlineData("live")]
    [InlineData("main")]
    public void ABaseEnvironmentThatLooksLikeProductionWarnsLoudly(string name)
    {
        // Section 18: base previews off a staging environment, not production, so preview secrets are
        // never real ones. A PR environment replicates every variable from its base, so this is the
        // difference between a preview and a copy of production anybody with a change request can reach.
        var options = DeploymentOptions.Parse(Railway(name));

        var warning = Assert.Single(options.Warnings);

        Assert.Equal("CHARTER_RAILWAY_BASE_ENVIRONMENT", warning.Variable);
        Assert.Equal(ConfigSeverity.Warning, warning.Severity);
        Assert.Contains("replicates", warning.Text, StringComparison.Ordinal);
        Assert.Contains("staging", warning.Text, StringComparison.Ordinal);

        // A warning, never an error: an operator who really means it is not locked out of their own
        // instance, they are told what they have done.
        Assert.Empty(options.Errors);
        Assert.True(options.Railway?.BaseLooksLikeProduction);
    }

    [Fact]
    public async Task TheProductionWarningReachesTheLogAtStartup()
    {
        var options = DeploymentOptions.Parse(Railway("production"));
        var logger = new RecordingLogger<DeploymentStartupWarnings>();

        var warnings = new DeploymentStartupWarnings(options, logger);
        await warnings.StartAsync(TestContext.Current.CancellationToken);

        var logged = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Contains("CHARTER_RAILWAY_BASE_ENVIRONMENT", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RailwayVariablesSetWithoutTheProviderSayTheyAreDoingNothing()
    {
        var options = DeploymentOptions.Parse(Env(("CHARTER_RAILWAY_TOKEN", "railway-token")));

        var warning = Assert.Single(options.Warnings);

        Assert.Equal("CHARTER_RAILWAY_TOKEN", warning.Variable);
        Assert.Contains("CHARTER_DEPLOYMENT_PROVIDER", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewLifetimeDefaultsToSeventyTwoHoursAndZeroMeansNever()
    {
        Assert.Equal(TimeSpan.FromHours(72), DeploymentOptions.Parse(Env()).PreviewTtl);

        var never = DeploymentOptions.Parse(Env(("CHARTER_PREVIEW_TTL_HOURS", "0")));

        Assert.Equal(TimeSpan.Zero, never.PreviewTtl);
        Assert.Null(never.ExpiryFor(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ThePreviewLifetimeIsStampedFromWhenThePreviewBecameReady()
    {
        var options = DeploymentOptions.Parse(Env(("CHARTER_PREVIEW_TTL_HOURS", "8")));
        var now = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddHours(8), options.ExpiryFor(now));

        // A provider that states its own lifetime wins over the operator's default.
        Assert.Equal(now.AddHours(1), options.ExpiryFor(now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void AnUnknownProviderIsRejectedRatherThanIgnored()
    {
        var options = DeploymentOptions.Parse(Env(("CHARTER_DEPLOYMENT_PROVIDER", "heroku")));

        var error = Assert.Single(options.Errors);

        Assert.Equal("CHARTER_DEPLOYMENT_PROVIDER", error.Variable);
    }
}
