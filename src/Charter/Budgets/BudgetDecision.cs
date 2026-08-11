using System.Globalization;
using Charter.Domain;

namespace Charter.Budgets;

/// <summary>
/// What happened when the work was put to the budgets (sections 34.4, 34.5).
/// </summary>
public enum BudgetOutcome
{
    /// <summary>Headroom everywhere it needed it. The hold is taken.</summary>
    Allowed,

    /// <summary>Over a <c>warn</c> budget. Proceeds; the budget owner is told (section 34.5).</summary>
    Warned,

    /// <summary>
    /// Falls back to the approval queue instead of failing (sections 34.5, 7.5). The best default
    /// for an org that spends freely: work does not stop, it acquires a human decision.
    /// </summary>
    RequiresApproval,

    /// <summary>Routes to a cheaper model tier and labels the session accordingly.</summary>
    DowngradeModel,

    /// <summary>Held until the period rolls over, showing the date.</summary>
    QueuedUntilReset,

    /// <summary>Refused, with the exact figure and who can raise it. The crudest option.</summary>
    Blocked,
}

/// <summary>One budget's arithmetic, as the decision reports it (section 34.8).</summary>
/// <param name="BudgetId">The budget.</param>
/// <param name="Name">Its name, for the message.</param>
/// <param name="Unit">Which currency it is denominated in (section 34.1).</param>
/// <param name="Amount">The cap.</param>
/// <param name="Committed">Settled spend plus live reservations inside the current period.</param>
/// <param name="Required">What this work would take from it.</param>
/// <param name="Behaviour">What it does at the limit.</param>
/// <param name="ResetsAt">When the current period rolls over.</param>
/// <param name="ApprovalThreshold">
/// Spend above this needs an approver, below it flows (section 34.2). Null means no threshold.
/// </param>
/// <param name="ExemptByReserve">
/// True when the work fitted inside a guaranteed floor beneath this budget and so was not charged to
/// it (section 34.3).
/// </param>
public sealed record BudgetConstraint(
    Guid BudgetId,
    string Name,
    LedgerUnit Unit,
    decimal Amount,
    decimal Committed,
    decimal Required,
    BudgetBehaviour Behaviour,
    DateTimeOffset ResetsAt,
    decimal? ApprovalThreshold = null,
    bool ExemptByReserve = false)
{
    /// <summary>What is left before the cap.</summary>
    public decimal Headroom => Math.Max(0m, Amount - Committed);

    /// <summary>Whether this budget has room for the work.</summary>
    public bool HasHeadroom => ExemptByReserve || Committed + Required <= Amount;

    /// <summary>How far into the period the budget is, as a fraction. Section 34.8's alerts.</summary>
    public double Utilisation => Amount <= 0m ? 1d : (double)((Committed + Required) / Amount);
}

/// <summary>An alert threshold this work crossed (section 34.8).</summary>
/// <param name="BudgetId">The budget.</param>
/// <param name="Name">Its name.</param>
/// <param name="Threshold">The fraction crossed, such as 0.9.</param>
/// <param name="Utilisation">Where the budget now stands.</param>
public sealed record BudgetAlert(Guid BudgetId, string Name, double Threshold, double Utilisation);

/// <summary>
/// The answer to "may this run, and what does it cost".
/// </summary>
/// <remarks>
/// <para>
/// Section 7.5 lists <em>budget caps</em> among the things auto-dispatch never bypasses. This is the
/// thing that makes that true: the gate answers whether a human needs to vet the specification, and
/// this answers whether there is money to run it, and neither substitutes for the other.
/// </para>
/// <para>
/// Every refusal carries <see cref="Message"/>, and every message names who can raise the limit
/// (section 34.5). That is checked by a test rather than left to whoever writes the next one.
/// </para>
/// </remarks>
public sealed record BudgetDecision
{
    /// <summary>What happened.</summary>
    public required BudgetOutcome Outcome { get; init; }

    /// <summary>Whether the work may start now.</summary>
    public bool Permitted => Outcome is BudgetOutcome.Allowed or BudgetOutcome.Warned;

    /// <summary>The reservation, when one was taken. Settle or release it (section 34.4).</summary>
    public Guid? LedgerEntryId { get; init; }

    /// <summary>What the work was expected to cost.</summary>
    public required BudgetEstimate Estimate { get; init; }

    /// <summary>Every budget that governs this work, whether or not it had room (section 34.3).</summary>
    public IReadOnlyList<BudgetConstraint> Constraints { get; init; } = [];

    /// <summary>The budgets that did not have room, in the order they were evaluated.</summary>
    public IReadOnlyList<BudgetConstraint> Breached => [.. Constraints.Where(c => !c.HasHeadroom)];

    /// <summary>Plain language, safe to show a requester (section 11). Empty when allowed outright.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Thresholds this work crossed, for the budget owner's alert (section 34.8).</summary>
    public IReadOnlyList<BudgetAlert> Alerts { get; init; } = [];

    /// <summary>When a queued session's period rolls over. Never an ETA for the work itself.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>The cheaper model a <c>downgrade_model</c> budget routes to (section 34.5).</summary>
    public string? DowngradeToModel { get; init; }

    /// <summary>
    /// Ungoverned: no budget applies. Section 34.9's personal mode reaches this by having no budget
    /// rows at all rather than by a branch that skips the check.
    /// </summary>
    public static BudgetDecision Ungoverned(BudgetEstimate estimate, Guid? ledgerEntryId) => new()
    {
        Outcome = BudgetOutcome.Allowed,
        Estimate = estimate,
        LedgerEntryId = ledgerEntryId,
    };
}

/// <summary>
/// The sentences a limit produces. One place, because section 34.5's rule is a property of every
/// message rather than of any one caller.
/// </summary>
public static class BudgetLimitMessage
{
    /// <summary>
    /// Describes a budget with no room, always ending in who can raise it.
    /// </summary>
    /// <param name="constraint">The budget that ran out.</param>
    /// <param name="authority">Who to ask.</param>
    /// <param name="estimate">What the work was estimated at, for the unpriced caveat.</param>
    public static string ForBreach(
        BudgetConstraint constraint,
        BudgetAuthorityDescription authority,
        BudgetEstimate? estimate = null)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        var lead = constraint.Behaviour switch
        {
            BudgetBehaviour.Warn =>
                $"This is over the \"{constraint.Name}\" budget: "
                + $"{Amount(constraint.Committed + constraint.Required, constraint.Unit)} of "
                + $"{Amount(constraint.Amount, constraint.Unit)}. It is running anyway, and the "
                + "budget's owner has been told.",

            BudgetBehaviour.RequireApproval =>
                $"This needs someone to approve the spend: it would take \"{constraint.Name}\" to "
                + $"{Amount(constraint.Committed + constraint.Required, constraint.Unit)} of "
                + $"{Amount(constraint.Amount, constraint.Unit)}. It is waiting in the approval "
                + "queue rather than being refused.",

            BudgetBehaviour.DowngradeModel =>
                $"\"{constraint.Name}\" has {Amount(constraint.Headroom, constraint.Unit)} left of "
                + $"{Amount(constraint.Amount, constraint.Unit)}, so this runs on a cheaper model "
                + "and is labelled as having done so.",

            BudgetBehaviour.QueueUntilReset =>
                $"\"{constraint.Name}\" is spent — {Amount(constraint.Committed, constraint.Unit)} of "
                + $"{Amount(constraint.Amount, constraint.Unit)}. This is held until the budget "
                + $"resets on {Date(constraint.ResetsAt)}.",

            _ =>
                $"\"{constraint.Name}\" has {Amount(constraint.Headroom, constraint.Unit)} left of "
                + $"{Amount(constraint.Amount, constraint.Unit)} and this needs "
                + $"{Amount(constraint.Required, constraint.Unit)}, so it cannot start. The budget "
                + $"resets on {Date(constraint.ResetsAt)}.",
        };

        var caveat = estimate?.Basis == BudgetEstimateBasis.Unpriced
            ? " Nothing here knows what that model costs, so the figure above is what is already "
                + "committed rather than what this would add."
            : string.Empty;

        return lead + caveat + " " + authority.Sentence;
    }

    /// <summary>
    /// The message for spend over a budget's approval threshold while it still has headroom.
    /// </summary>
    /// <remarks>
    /// Section 34.2's <c>approval_threshold</c>: <em>spend above this needs an approver, below it
    /// flows</em>. This is not a breach — the money is there — so the sentence says so, because
    /// "waiting for a person" and "out of money" need different reactions from the requester.
    /// </remarks>
    public static string ForThreshold(
        BudgetConstraint constraint,
        decimal threshold,
        BudgetAuthorityDescription authority)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        return $"\"{constraint.Name}\" lets spend up to {Amount(threshold, constraint.Unit)} run "
            + $"without asking, and this is estimated at {Amount(constraint.Required, constraint.Unit)}, "
            + "so it is waiting for someone to approve the spend. There is budget for it. "
            + authority.Sentence;
    }

    /// <summary>
    /// Formats an amount in its own currency (section 34.1). Never conflates the two.
    /// </summary>
    /// <remarks>
    /// The dollar sign is written rather than taken from a culture: the build sets
    /// <c>InvariantGlobalization</c>, provider billing is USD regardless of where the instance runs,
    /// and section 34.8's per-org display currency is a presentation conversion applied above this
    /// with its rate and date shown — not a different number here.
    /// </remarks>
    public static string Amount(decimal value, LedgerUnit unit) => unit switch
    {
        LedgerUnit.Usd => string.Create(CultureInfo.InvariantCulture, $"${value:0.00}"),
        _ => value == 1m
            ? "1 session"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} sessions"),
    };

    private static string Date(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}
