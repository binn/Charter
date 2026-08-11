using Charter.Budgets;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hosting;
using Charter.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterBudgets()</c> registers everything section 34 needs, and gives way to a host that
/// configured something first.
/// </summary>
public class BudgetWiringTests
{
    [Fact]
    public void TheEstimatorEvaluatorAndAuthorityResolveFromTheContainer()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<BudgetEstimator>(scope.ServiceProvider.GetRequiredService<IBudgetEstimator>());
        Assert.IsType<BudgetEvaluator>(scope.ServiceProvider.GetRequiredService<IBudgetEvaluator>());
        Assert.IsType<BudgetAuthority>(scope.ServiceProvider.GetRequiredService<IBudgetAuthority>());
    }

    [Fact]
    public void TheReservationSweeperIsHosted()
    {
        // Section 34.4's TTL means nothing unless something enforces it. Registering the sweeper as
        // a hosted service is what turns "reservations expire" from a comment into behaviour.
        using var provider = Services().BuildServiceProvider();

        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is BudgetReservationSweeper);
    }

    [Fact]
    public void ConfigurationRunsOverTheDefaults()
    {
        using var provider = Services(options => options.ReservationTtl = TimeSpan.FromMinutes(7))
            .BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(7), provider.GetRequiredService<BudgetOptions>().ReservationTtl);
    }

    [Fact]
    public void AHostThatSuppliedItsOwnOptionsKeepsThem()
    {
        var services = new ServiceCollection();
        var mine = new BudgetOptions { HistorySampleSize = 3 };

        services.AddSingleton(mine);
        Wire(services);

        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<BudgetOptions>());
    }

    [Fact]
    public void TheShippedDefaultsMatchSection349()
    {
        var options = new BudgetOptions();

        // Personal mode: no budgets at all. One person, their own credentials, nothing to govern.
        Assert.Null(BudgetDefaults.For(Organization.Create("personal"), options));

        var org = BudgetDefaults.For(
            Organization.Create("acme", OrganizationMode.Organization),
            options);

        Assert.NotNull(org);
        Assert.Equal(BudgetBehaviour.RequireApproval, org.Behaviour);
        Assert.Equal(LedgerUnit.Usd, org.Unit);
        Assert.Equal(BudgetPeriod.Monthly, org.Period);
        Assert.Equal(options.DefaultApprovalThresholdUsd, org.ApprovalThreshold);
    }

    [Fact]
    public void BlockingIsNeverTheShippedDefault()
    {
        // Section 34.5: blocking is the crudest option and rarely the right default. Nothing Charter
        // ships turned on should refuse work outright.
        var org = BudgetDefaults.For(
            Organization.Create("acme", OrganizationMode.Organization),
            new BudgetOptions());

        Assert.NotEqual(BudgetBehaviour.Block, org!.Behaviour);
    }

    /// <summary>
    /// The host's projection of section 4.2 beats <c>AddCharterBudgets</c>'s own <c>TryAdd</c>.
    /// </summary>
    /// <remarks>
    /// This is the assertion the budget subsystem was missing. <c>AddCharterBudgets</c> has always
    /// said, in a comment, that a host projecting <c>CharterConfig</c> could register first and win -
    /// and no host did, so <c>CHARTER_DEFAULT_SESSION_BUDGET_USD</c> and
    /// <c>CHARTER_DEFAULT_MONTHLY_BUDGET_USD</c> parsed, validated, warned about each other, and
    /// reached a hardcoded 5 and 500 instead.
    /// </remarks>
    [Fact]
    public void TheConfiguredCapsBeatTheHardcodedOnes()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_DEFAULT_SESSION_BUDGET_USD", "12.50"),
            ("CHARTER_DEFAULT_MONTHLY_BUDGET_USD", "2500"));

        var services = new ServiceCollection();
        services.AddCharterBudgetLimits(config);
        Wire(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<BudgetOptions>();

        Assert.Equal(12.50m, options.DefaultApprovalThresholdUsd);
        Assert.Equal(2500m, options.DefaultOrganizationAmountUsd);

        // And they reach the row, not just the options record.
        var budget = BudgetDefaults.For(
            Organization.Create("acme", OrganizationMode.Organization),
            options);

        Assert.Equal(2500m, budget!.Amount);
        Assert.Equal(12.50m, budget.ApprovalThreshold);
    }

    /// <summary>An instance that sets nothing gets exactly what section 4.2's table promises.</summary>
    [Fact]
    public void TheProjectedDefaultsAreTheOnesSection42Documents()
    {
        var options = BudgetLimitsServiceCollectionExtensions.From(ConfigTestEnvironment.Valid());

        Assert.Equal(5.00m, options.DefaultApprovalThresholdUsd);
        Assert.Equal(100.00m, options.DefaultOrganizationAmountUsd);

        // And the record's own defaults agree with them, so the TryAdd fallback a subsystem graph
        // gets is the same instance an unconfigured host would have projected. They disagreed until
        // now - 500 against a documented 100 - which was invisible while nothing projected either.
        var fallback = new BudgetOptions();

        Assert.Equal(options.DefaultApprovalThresholdUsd, fallback.DefaultApprovalThresholdUsd);
        Assert.Equal(options.DefaultOrganizationAmountUsd, fallback.DefaultOrganizationAmountUsd);
    }

    private static ServiceCollection Services(Action<BudgetOptions>? configure = null)
    {
        var services = new ServiceCollection();
        Wire(services, configure);

        return services;
    }

    private static void Wire(ServiceCollection services, Action<BudgetOptions>? configure = null)
    {
        services.AddLogging();
        services.AddCharterData(DatabaseUrl.ToNpgsql(CharterDbContextFactory.LocalDevelopmentUrl));
        services.AddSingleton<IModelPriceCatalog>(new StaticModelPriceTable());
        services.AddCharterBudgets(configure);
    }
}
