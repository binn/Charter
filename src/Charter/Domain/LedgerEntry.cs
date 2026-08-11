namespace Charter.Domain;

/// <summary>The cost categories of sections 5 and 34.6. Budgets can target them individually.</summary>
public enum LedgerCategory
{
    /// <summary>The agent run itself.</summary>
    Build,

    /// <summary>Walkthroughs and annotations. Its own line, or an admin cuts it first (section 13).</summary>
    Teach,

    Refine,

    Recap,

    Recon,

    /// <summary>Far more expensive than a feature tweak; deserves separate authorisation (section 26.8).</summary>
    Scaffold,

    /// <summary>
    /// Should be generously funded or uncapped. A chat that answers <em>it already does that</em>
    /// saves an entire build; rationing it pushes people straight to building (section 34.6).
    /// </summary>
    Chat,
}

/// <summary>
/// The two currencies of section 34.1. Never conflate them.
/// </summary>
public enum LedgerUnit
{
    /// <summary>Metered API keys and OpenRouter. Real marginal cost.</summary>
    Usd,

    /// <summary>Subscription-backed credentials. No marginal cost, but a scarce shared resource.</summary>
    QuotaSessions,
}

/// <summary>Reserve, then settle — the concurrency-safe accounting of section 34.4.</summary>
public enum LedgerState
{
    /// <summary>Held against every applicable budget before dispatch, with a TTL.</summary>
    Reserved,

    /// <summary>Actual cost recorded on completion.</summary>
    Settled,

    /// <summary>Cancelled, failed, or the reservation expired.</summary>
    Released,
}

/// <summary>
/// One accounting line (section 5), denominated in one unit but always reporting both.
/// </summary>
/// <remarks>
/// Section 20b.5 is the reason this type looks the way it does: a subscription-backed session has no
/// marginal cost and still consumes scarce quota, and reporting it as <c>$0.00</c> makes budget
/// dashboards lie. So every entry carries <see cref="Usd"/>, <see cref="QuotaSessions"/> and
/// <see cref="ImputedUsd"/>. <see cref="Unit"/> says which of the first two the entry is denominated
/// in — which budgets it debits — and <see cref="Amount"/> derives from it rather than being stored,
/// so the denominating amount and the per-unit columns cannot drift apart.
/// </remarks>
public sealed class LedgerEntry
{
    private LedgerEntry()
    {
    }

    private LedgerEntry(
        Guid id,
        Guid orgId,
        Guid userId,
        Guid? sessionId,
        LedgerCategory category,
        LedgerUnit unit,
        decimal usd,
        decimal quotaSessions,
        decimal imputedUsd,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        UserId = userId;
        SessionId = sessionId;
        Category = category;
        Unit = unit;
        Usd = usd;
        QuotaSessions = quotaSessions;
        ImputedUsd = imputedUsd;
        State = LedgerState.Reserved;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Null for spend with no session behind it — chat and refinement before dispatch.</summary>
    public Guid? SessionId { get; private set; }

    /// <summary>
    /// Every budget this entry was reserved against. Section 34.3: a session must have headroom in
    /// <em>every</em> applicable budget, not the most specific one, so the reservation fans out.
    /// </summary>
    public IReadOnlyList<Guid> BudgetIds { get; private set; } = [];

    public LedgerCategory Category { get; private set; }

    public LedgerUnit Unit { get; private set; }

    /// <summary>Real dollars. Zero for a subscription-backed entry.</summary>
    public decimal Usd { get; private set; }

    /// <summary>Quota consumed against the credential owner. Zero for a metered entry.</summary>
    public decimal QuotaSessions { get; private set; }

    /// <summary>
    /// What this work would have cost on a metered API. Equals <see cref="Usd"/> for metered spend,
    /// and makes the value of a subscription visible rather than invisible (section 34.1).
    /// </summary>
    public decimal ImputedUsd { get; private set; }

    /// <summary>The amount in the entry's own unit. Derived, never stored.</summary>
    public decimal Amount => Unit == LedgerUnit.Usd ? Usd : QuotaSessions;

    /// <summary>
    /// What the estimator predicted in dollars when the hold was taken.
    /// </summary>
    /// <remarks>
    /// Kept after settlement, when <see cref="Usd"/> has been overwritten with the actual. Section
    /// 34.4: <em>estimates improve over time — store actual-versus-estimate per repo and per project
    /// type and use the running distribution rather than a fixed heuristic</em>. Without this column
    /// the only record of the prediction is destroyed by the settlement that would have graded it.
    /// </remarks>
    public decimal EstimatedUsd { get; private set; }

    /// <summary>What the estimator predicted in quota when the hold was taken.</summary>
    public decimal EstimatedQuotaSessions { get; private set; }

    /// <summary>
    /// Actual minus estimate in the entry's own unit, once settled. Positive means the estimator
    /// under-predicted, which is the direction that lets a cap be breached.
    /// </summary>
    public decimal? EstimateError => State != LedgerState.Settled
        ? null
        : Amount - (Unit == LedgerUnit.Usd ? EstimatedUsd : EstimatedQuotaSessions);

    public LedgerState State { get; private set; }

    /// <summary>Reservations expire so a crashed orchestrator does not strand budget (section 34.4).</summary>
    public DateTimeOffset? ReservedUntil { get; private set; }

    public Guid? CredentialGrantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Reserves metered spend before dispatch.</summary>
    public static LedgerEntry ReserveUsd(
        Guid orgId,
        Guid userId,
        LedgerCategory category,
        decimal usd,
        IEnumerable<Guid>? budgetIds = null,
        Guid? sessionId = null,
        Guid? credentialGrantId = null,
        DateTimeOffset? reservedUntil = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usd);

        return new LedgerEntry(
            id ?? Guid.CreateVersion7(),
            orgId,
            userId,
            sessionId,
            category,
            LedgerUnit.Usd,
            usd,
            quotaSessions: 0m,
            imputedUsd: usd,
            DomainTime.Resolve(now))
        {
            BudgetIds = budgetIds is null ? [] : [.. budgetIds.Distinct()],
            CredentialGrantId = credentialGrantId,
            ReservedUntil = DomainTime.ResolveOptional(reservedUntil),
            EstimatedUsd = usd,
        };
    }

    /// <summary>
    /// Reserves subscription quota. <paramref name="imputedUsd"/> is required: without it the
    /// dashboard shows a free session and the org cannot see what its subscription is worth.
    /// </summary>
    public static LedgerEntry ReserveQuota(
        Guid orgId,
        Guid userId,
        LedgerCategory category,
        decimal quotaSessions,
        decimal imputedUsd,
        IEnumerable<Guid>? budgetIds = null,
        Guid? sessionId = null,
        Guid? credentialGrantId = null,
        DateTimeOffset? reservedUntil = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quotaSessions);
        ArgumentOutOfRangeException.ThrowIfNegative(imputedUsd);

        return new LedgerEntry(
            id ?? Guid.CreateVersion7(),
            orgId,
            userId,
            sessionId,
            category,
            LedgerUnit.QuotaSessions,
            usd: 0m,
            quotaSessions,
            imputedUsd,
            DomainTime.Resolve(now))
        {
            BudgetIds = budgetIds is null ? [] : [.. budgetIds.Distinct()],
            CredentialGrantId = credentialGrantId,
            ReservedUntil = DomainTime.ResolveOptional(reservedUntil),
            EstimatedQuotaSessions = quotaSessions,
            EstimatedUsd = imputedUsd,
        };
    }

    /// <summary>
    /// Settles the reservation against what actually happened, releasing the difference
    /// (section 34.4). Both units are restated so neither can drift from the other.
    /// </summary>
    public void Settle(decimal usd, decimal quotaSessions, decimal imputedUsd, DateTimeOffset? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usd);
        ArgumentOutOfRangeException.ThrowIfNegative(quotaSessions);
        ArgumentOutOfRangeException.ThrowIfNegative(imputedUsd);

        if (State != LedgerState.Reserved)
        {
            throw new InvalidOperationException($"A {State} ledger entry cannot be settled.");
        }

        Usd = usd;
        QuotaSessions = quotaSessions;
        ImputedUsd = imputedUsd;
        State = LedgerState.Settled;
        ReservedUntil = null;
        SettledAt = DomainTime.Resolve(now);
    }

    /// <summary>Releases the hold on cancellation, failure, or reservation expiry.</summary>
    public void Release(DateTimeOffset? now = null)
    {
        if (State == LedgerState.Settled)
        {
            throw new InvalidOperationException("A settled ledger entry cannot be released.");
        }

        Usd = 0m;
        QuotaSessions = 0m;
        ImputedUsd = 0m;
        State = LedgerState.Released;
        ReservedUntil = null;
        SettledAt = DomainTime.Resolve(now);
    }
}
