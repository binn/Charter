using Charter.Budgets;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
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
