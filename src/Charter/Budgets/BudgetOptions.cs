namespace Charter.Budgets;

/// <summary>
/// Knobs for the budget evaluator (section 34).
/// </summary>
/// <remarks>
/// Registered as a singleton with defaults. Nothing here is an environment variable: section 34
/// budgets are rows an admin edits, not deployment configuration, and the two things an operator
/// might want to change here — how long a hold lives, and how many past sessions the estimator
/// learns from — are properties of the installation rather than of any one budget.
/// </remarks>
public sealed class BudgetOptions
{
    /// <summary>
    /// How long a reservation is honoured before it stops counting against a budget.
    /// </summary>
    /// <remarks>
    /// Section 34.4: reservations expire on a TTL so a crashed orchestrator does not strand budget.
    /// Two hours is longer than any session Charter dispatches and short enough that a container
    /// that died mid-run does not hold a team's monthly cap hostage until somebody notices.
    /// </remarks>
    public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How many settled sessions the estimator averages over. Section 34.4.</summary>
    public int HistorySampleSize { get; set; } = 20;

    /// <summary>
    /// Below this many historical samples the estimator does not trust the history and prices the
    /// work from the model's published rates instead.
    /// </summary>
    public int MinimumHistorySamples { get; set; } = 3;

    /// <summary>
    /// The per-session spend an organisation-default budget lets through without an approver.
    /// <c>CHARTER_DEFAULT_SESSION_BUDGET_USD</c>.
    /// </summary>
    /// <remarks>
    /// Section 34.9: <em>org-level <c>require_approval</c> above a modest per-session threshold</em>.
    /// Modest is the point — the default exists so nobody is surprised by a four-figure month, not
    /// so every request waits on somebody.
    /// </remarks>
    public decimal DefaultApprovalThresholdUsd { get; set; } = 5.00m;

    /// <summary>
    /// The amount an organisation-default budget is created with.
    /// <c>CHARTER_DEFAULT_MONTHLY_BUDGET_USD</c>.
    /// </summary>
    /// <remarks>
    /// These two are the only values here section 4.2 gives a variable to, and their defaults are
    /// section 4.2's rather than numbers of this file's own choosing. They used to disagree — 500
    /// here against a documented 100 — which nobody noticed, because no host projected the variables
    /// into this record at all and the documented figure was reaching nothing.
    /// </remarks>
    public decimal DefaultOrganizationAmountUsd { get; set; } = 100.00m;

    /// <summary>
    /// The cheaper model a <c>downgrade_model</c> budget routes to when it has no repo-specific
    /// preference. Null leaves the choice to the caller, which then has to decide or refuse.
    /// </summary>
    public string? DowngradeModel { get; set; } = "anthropic/claude-haiku-4-5";
}
