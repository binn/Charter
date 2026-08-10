using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>What a budget is attached to (section 34.2).</summary>
public enum BudgetScopeType
{
    Org,

    Team,

    Repo,

    Project,

    User,

    Role,

    Tag,
}

/// <summary>Budget periods, including the fiscal and one-off shapes real organisations use.</summary>
public enum BudgetPeriod
{
    Daily,

    Weekly,

    Monthly,

    Quarterly,

    [EnumMember(Value = "rolling_30d")]
    Rolling30Days,

    FiscalYear,

    /// <summary>Campaign budgets, bounded by <c>starts_at</c> and <c>ends_at</c> (section 34.7).</summary>
    OneOff,
}

/// <summary>
/// What happens at the limit (section 34.5). Blocking is the crudest option and rarely the right
/// default.
/// </summary>
public enum BudgetBehaviour
{
    /// <summary>Proceeds; notifies the budget owner.</summary>
    Warn,

    /// <summary>
    /// Falls back to the approval queue instead of failing. The best default for an org that spends
    /// freely: work does not stop, it acquires a human decision above a threshold.
    /// </summary>
    RequireApproval,

    /// <summary>Routes to a cheaper model tier and labels the session accordingly.</summary>
    DowngradeModel,

    /// <summary>Holds the session until the period rolls over, showing the date.</summary>
    QueueUntilReset,

    /// <summary>Refuses, with the exact figure and who can raise it.</summary>
    Block,
}

/// <summary>Whether unspent budget carries into the next period (section 34.2).</summary>
public enum BudgetRollover
{
    None,

    Full,

    Capped,
}

/// <summary>
/// A spend ceiling with real internal structure (section 34.2): departments, cost centres, projects
/// with their own funding, one-off pushes, and people trusted with far more than the default.
/// </summary>
/// <remarks>
/// Budgets nest, and section 34.3 evaluates them together rather than most-specific-wins: a session
/// must have headroom in <em>every</em> applicable budget. <see cref="ReservedAmount"/> guarantees a
/// floor beneath the shared pool, which is the pattern large teams actually need — everyone gets a
/// guaranteed minimum, and the rest is first-come.
/// </remarks>
public sealed class Budget
{
    private Budget()
    {
    }

    private Budget(
        Guid id,
        Guid orgId,
        string name,
        BudgetScopeType scopeType,
        string? scopeId,
        LedgerUnit unit,
        BudgetPeriod period,
        decimal amount,
        BudgetBehaviour behaviour,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        Name = name;
        ScopeType = scopeType;
        ScopeId = scopeId;
        Unit = unit;
        Period = period;
        Amount = amount;
        Behaviour = behaviour;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public BudgetScopeType ScopeType { get; private set; }

    /// <summary>
    /// Text rather than a foreign key, because the scope is not always a row: a role scope holds a
    /// role name and a tag scope holds a tag. Section 34.2 writes it as one <c>scope_id</c> column
    /// across all seven scope types, so this is the only shape that fits without a second column.
    /// </summary>
    public string? ScopeId { get; private set; }

    public LedgerUnit Unit { get; private set; }

    /// <summary>Empty means every category (section 34.2).</summary>
    public IReadOnlyList<LedgerCategory> Categories { get; private set; } = [];

    public BudgetPeriod Period { get; private set; }

    /// <summary>Fiscal year start or billing day.</summary>
    public DateTimeOffset? PeriodAnchor { get; private set; }

    public decimal Amount { get; private set; }

    public BudgetBehaviour Behaviour { get; private set; }

    /// <summary>Spend above this needs an approver; below it flows.</summary>
    public decimal? ApprovalThreshold { get; private set; }

    public BudgetRollover Rollover { get; private set; }

    public decimal? RolloverCap { get; private set; }

    /// <summary>A guaranteed floor before pooled spend is touched (section 34.3).</summary>
    public decimal ReservedAmount { get; private set; }

    public DateTimeOffset? StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    /// <summary>Fractions of the amount, such as <c>0.5, 0.75, 0.9, 1.0</c>.</summary>
    public IReadOnlyList<double> AlertThresholds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Budget Create(
        Guid orgId,
        string name,
        BudgetScopeType scopeType,
        LedgerUnit unit,
        BudgetPeriod period,
        decimal amount,
        BudgetBehaviour behaviour = BudgetBehaviour.RequireApproval,
        string? scopeId = null,
        IEnumerable<LedgerCategory>? categories = null,
        DateTimeOffset? periodAnchor = null,
        decimal? approvalThreshold = null,
        BudgetRollover rollover = BudgetRollover.None,
        decimal? rolloverCap = null,
        decimal reservedAmount = 0m,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        IEnumerable<double>? alertThresholds = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedAmount);

        if (reservedAmount > amount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedAmount),
                reservedAmount,
                "A reserved floor cannot exceed the budget itself.");
        }

        if (rollover == BudgetRollover.Capped && rolloverCap is null)
        {
            throw new ArgumentException("A capped rollover needs a cap.", nameof(rolloverCap));
        }

        if (period == BudgetPeriod.OneOff && (startsAt is null || endsAt is null))
        {
            throw new ArgumentException("A one-off budget needs a start and an end.", nameof(period));
        }

        if (startsAt is not null && endsAt is not null && endsAt <= startsAt)
        {
            throw new ArgumentException("A budget window must end after it starts.", nameof(endsAt));
        }

        return new Budget(
            id ?? Guid.CreateVersion7(),
            orgId,
            name.Trim(),
            scopeType,
            string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim(),
            unit,
            period,
            amount,
            behaviour,
            DomainTime.Resolve(now))
        {
            Categories = categories is null ? [] : [.. categories.Distinct().Order()],
            PeriodAnchor = DomainTime.ResolveOptional(periodAnchor),
            ApprovalThreshold = approvalThreshold,
            Rollover = rollover,
            RolloverCap = rolloverCap,
            ReservedAmount = reservedAmount,
            StartsAt = DomainTime.ResolveOptional(startsAt),
            EndsAt = DomainTime.ResolveOptional(endsAt),
            AlertThresholds = alertThresholds is null ? [] : [.. alertThresholds.Distinct().Order()],
        };
    }

    /// <summary>An empty category list means the budget governs every category (section 34.2).</summary>
    public bool Covers(LedgerCategory category) => Categories.Count == 0 || Categories.Contains(category);

    public bool IsActiveAt(DateTimeOffset instant)
        => (StartsAt is null || StartsAt <= instant) && (EndsAt is null || EndsAt > instant);

    /// <summary>A named top-up, which section 34.7 requires to be audited rather than silent.</summary>
    public void TopUp(decimal additional, DateTimeOffset? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additional);
        Amount += additional;
        UpdatedAt = DomainTime.Resolve(now);
    }

    public void SetBehaviour(BudgetBehaviour behaviour, DateTimeOffset? now = null)
    {
        Behaviour = behaviour;
        UpdatedAt = DomainTime.Resolve(now);
    }
}
