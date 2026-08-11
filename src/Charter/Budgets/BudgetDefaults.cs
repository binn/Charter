using Charter.Domain;

namespace Charter.Budgets;

/// <summary>
/// Section 34.9's shipped defaults.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Personal mode gets nothing.</strong> One person, their own credentials, nothing to
/// govern — so there is deliberately no <c>ForPersonal</c> method here to call by accident. A
/// personal instance has no budget rows, the conjunction of section 34.3 runs over an empty set, and
/// every session is allowed without a single branch on the organisation's mode (section 7.2).
/// </para>
/// <para>
/// <strong>An organisation gets one budget, and it does not block.</strong>
/// <em>Ship with governance available but not imposed. An org that wants structure builds it; an org
/// that doesn't is never blocked by a limit it didn't set.</em> So the default is
/// <see cref="BudgetBehaviour.RequireApproval"/> above a modest per-session threshold: work keeps
/// moving and acquires a human decision, rather than stopping.
/// </para>
/// </remarks>
public static class BudgetDefaults
{
    /// <summary>The name the shipped organisation budget is created with.</summary>
    public const string OrganizationBudgetName = "Organisation default";

    /// <summary>
    /// The budget a new organisation-mode instance starts with, or <c>null</c> for personal mode.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing for personal mode is the point: a seeder can call this
    /// unconditionally and get section 34.9's table without writing the branch itself.
    /// </remarks>
    public static Budget? For(
        Organization organization,
        BudgetOptions options,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(options);

        if (organization.Mode == OrganizationMode.Personal)
        {
            return null;
        }

        return Budget.Create(
            organization.Id,
            OrganizationBudgetName,
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            options.DefaultOrganizationAmountUsd,
            BudgetBehaviour.RequireApproval,

            // Every category, including chat. Section 34.6: chat should be generously funded or
            // uncapped, and rationing it pushes people straight to building — so the shipped default
            // never singles it out, and an admin who adds a chat budget has decided to.
            categories: null,
            approvalThreshold: options.DefaultApprovalThresholdUsd,
            alertThresholds: [0.5, 0.75, 0.9, 1.0],
            now: now);
    }
}
