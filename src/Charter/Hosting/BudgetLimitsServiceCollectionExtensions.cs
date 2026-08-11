using Charter.Budgets;
using Charter.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Hosting;

/// <summary>
/// Projects the section 4.2 budget defaults onto the section 34 budget subsystem.
/// </summary>
/// <remarks>
/// <para>
/// <c>CHARTER_DEFAULT_SESSION_BUDGET_USD</c> and <c>CHARTER_DEFAULT_MONTHLY_BUDGET_USD</c> parsed,
/// validated - the parser even warns when a session cap exceeds the monthly one - and then reached
/// nothing. <c>AddCharterBudgets</c> registers <see cref="BudgetOptions"/> through <c>TryAdd</c> with
/// its own hardcoded 5 and 500, so an operator who set a cap got the hardcoded one instead. That is
/// the same defect as <c>CHARTER_MODEL_REFINE</c>, and it is fixed the same way: the host registers
/// the projection first, and the <c>TryAdd</c> below it loses.
/// </para>
/// <para>
/// Two of section 34.2's fields are what the two variables mean:
/// <see cref="BudgetOptions.DefaultApprovalThresholdUsd"/> is the per-session spend an
/// organisation-default budget lets through without an approver (section 34.9's <em>modest
/// per-session threshold</em>), and <see cref="BudgetOptions.DefaultOrganizationAmountUsd"/> is the
/// monthly amount that budget is created with. Everything else on <see cref="BudgetOptions"/> stays
/// at its shipped value, because section 4.2 gives it no variable and section 34 calls it a property
/// of the installation rather than of a budget.
/// </para>
/// </remarks>
public static class BudgetLimitsServiceCollectionExtensions
{
    /// <summary>Registers <see cref="BudgetOptions"/> from <paramref name="config"/>.</summary>
    /// <remarks>
    /// Must be called <em>before</em> <c>AddCharterBudgets</c>, which registers its defaults with
    /// <c>TryAdd</c>: the first registration wins and it has to be this one.
    /// </remarks>
    public static IServiceCollection AddCharterBudgetLimits(
        this IServiceCollection services,
        CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(From(config));

        return services;
    }

    /// <summary>
    /// The <see cref="BudgetOptions"/> section 4.2 describes, without a container to put it in.
    /// </summary>
    /// <remarks>
    /// Separate from the registration so the projection itself is testable, and so a caller that
    /// already holds a <see cref="CharterConfig"/> - a seeder, a test fixture - gets the same object
    /// the host registers rather than a second copy of the mapping.
    /// </remarks>
    public static BudgetOptions From(CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new BudgetOptions
        {
            DefaultApprovalThresholdUsd = config.Budgets.DefaultSessionUsd,
            DefaultOrganizationAmountUsd = config.Budgets.DefaultMonthlyUsd,
        };
    }
}
