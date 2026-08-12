using Charter;
using Charter.Budgets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers budgets and cost governance (section 34).
/// </summary>
/// <remarks>
/// Placed in <c>Microsoft.Extensions.DependencyInjection</c> so <c>AddCharterBudgets()</c> resolves
/// from <c>Program.cs</c> with no extra using directive, matching every other <c>Add*</c> in that
/// file.
/// </remarks>
public static class BudgetServiceCollectionExtensions
{
    /// <summary>
    /// Adds the estimator, the evaluator and the reservation sweeper.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">Overrides for <see cref="BudgetOptions"/>.</param>
    /// <remarks>
    /// <para>
    /// Call this <em>after</em> <c>AddCharterData()</c> and <c>AddCharterModels()</c>: the estimator
    /// resolves <c>CharterDbContext</c> for the historical distribution and <c>IModelPriceCatalog</c>
    /// for the model's price, rather than constructing either. Section 20b.6 is why the price comes
    /// from a catalog and not a table here — the budget estimator cannot work with a hardcoded price
    /// list when the model is user-selectable.
    /// </para>
    /// <para>
    /// The sweeper is a hosted service because section 34.4's TTL only means anything if something
    /// enforces it. It re-reads Postgres every cycle and holds nothing, so a container restart is a
    /// non-event (section 2.3).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterBudgets(
        this IServiceCollection services,
        Action<BudgetOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);

        services.TryAddSingleton(_ =>
        {
            var options = new BudgetOptions();
            configure?.Invoke(options);
            return options;
        });

        // Scoped, because all three hold a CharterDbContext and a reservation is one unit of work.
        services.TryAddScoped<IBudgetEstimator, BudgetEstimator>();
        services.TryAddScoped<IBudgetAuthority, BudgetAuthority>();
        services.TryAddScoped<IBudgetEvaluator, BudgetEvaluator>();

        services.AddHostedService<BudgetReservationSweeper>();

        return services;
    }
}
