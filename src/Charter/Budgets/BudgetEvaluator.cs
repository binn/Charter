using System.Data;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Budgets;

/// <summary>What is about to run, and against whose budgets.</summary>
public sealed record BudgetReservationRequest
{
    /// <summary>Everything the work belongs to (section 34.3).</summary>
    public required BudgetScopeSet Scope { get; init; }

    /// <summary>Which cost line this is (section 34.6).</summary>
    public required LedgerCategory Category { get; init; }

    /// <summary>The model, provider-qualified.</summary>
    public required string Model { get; init; }

    /// <summary>The session, once there is one.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>The credential that will serve the work, for attribution (section 20b.5).</summary>
    public Guid? CredentialGrantId { get; init; }

    /// <summary>True when a subscription grant will serve it: quota, not dollars.</summary>
    public bool SubscriptionBacked { get; init; }

    /// <summary>The approved spec's body, for scope.</summary>
    public string? SpecBodyMd { get; init; }

    /// <summary>How many acceptance criteria the spec carries.</summary>
    public int AcceptanceCriteria { get; init; }

    /// <summary>
    /// An estimate the caller already has. Left null the evaluator produces one, which is the normal
    /// path — a caller that estimates separately and reserves separately is a caller that can
    /// reserve a number nobody computed.
    /// </summary>
    public BudgetEstimate? Estimate { get; init; }
}

/// <summary>
/// Section 34.4's reserve-then-settle accounting, and section 7.5's budget cap.
/// </summary>
public interface IBudgetEvaluator
{
    /// <summary>
    /// Estimates the work and holds it against every applicable budget, inside one transaction with
    /// row locks (section 34.4 steps 1 and 2).
    /// </summary>
    Task<BudgetDecision> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles a hold against what actually happened, releasing the difference (step 3).
    /// </summary>
    Task<LedgerEntry?> SettleAsync(
        Guid ledgerEntryId,
        decimal usd,
        decimal quotaSessions,
        decimal imputedUsd,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a hold on cancellation or failure (step 4).</summary>
    Task<bool> ReleaseAsync(Guid ledgerEntryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases every hold whose TTL has passed, so a crashed orchestrator does not strand budget.
    /// </summary>
    Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The budget evaluator (section 34).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reserve, then settle.</strong> Section 34.4 borrows this from payment authorisation and
/// gives the reason plainly: without holds, ten concurrent sessions each pass the check and
/// collectively blow the cap. So the read of committed spend and the write of the new hold happen
/// inside one transaction that has already taken <c>FOR UPDATE</c> row locks on the organisation's
/// budgets. Concurrent reservations serialise on those locks; the tenth session sees the first nine.
/// </para>
/// <para>
/// <strong>Nesting is conjunctive.</strong> Section 34.3: a session must have headroom in
/// <em>every</em> applicable budget, not the most specific one. A user with $200 left inside an
/// exhausted team pool cannot spend. The one place precedence matters is
/// <see cref="Budget.ReservedAmount"/>, which is a guaranteed floor: spend inside a person's reserve
/// is not charged to the pools above them, which is what makes the floor a floor.
/// </para>
/// <para>
/// <strong>Personal mode is not a branch.</strong> Section 34.9 gives personal mode no budgets at
/// all — one person, their own credentials, nothing to govern — and that is expressed as an
/// organisation with no budget rows, so the conjunction over an empty set allows. There is no
/// <c>if (personalMode)</c> here and there must never be one (section 7.2). The spend is still
/// recorded, because section 34.8 shows cost on the artifact whether or not anything capped it.
/// </para>
/// </remarks>
public sealed class BudgetEvaluator : IBudgetEvaluator
{
    private readonly CharterDbContext _db;
    private readonly IBudgetEstimator _estimator;
    private readonly IBudgetAuthority _authority;
    private readonly BudgetOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BudgetEvaluator> _logger;

    /// <summary>Creates an evaluator.</summary>
    public BudgetEvaluator(
        CharterDbContext db,
        IBudgetEstimator estimator,
        IBudgetAuthority authority,
        BudgetOptions options,
        TimeProvider clock,
        ILogger<BudgetEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _estimator = estimator;
        _authority = authority;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BudgetDecision> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.GetUtcNow();
        var scope = request.Scope;

        var estimate = request.Estimate ?? await _estimator
            .EstimateAsync(
                new BudgetEstimateRequest
                {
                    OrgId = scope.OrgId,
                    RepoId = scope.RepoId,
                    Category = request.Category,
                    Model = request.Model,
                    SubscriptionBacked = request.SubscriptionBacked,
                    SpecBodyMd = request.SpecBodyMd,
                    AcceptanceCriteria = request.AcceptanceCriteria,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Read committed spend and write the hold under the same locks. An outer transaction is
        // joined rather than nested, so a caller that is already in one keeps its own boundary.
        var owned = _db.Database.CurrentTransaction is null;
        var transaction = owned
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false)
            : null;

        try
        {
            var budgets = await LockBudgetsAsync(scope.OrgId, cancellationToken).ConfigureAwait(false);

            var applicable = budgets
                .Where(budget => scope.IsGovernedBy(budget, request.Category, now))
                .OrderBy(budget => BudgetScopeSet.Specificity(budget.ScopeType))
                .ThenBy(budget => budget.Id)
                .ToList();

            if (applicable.Count == 0)
            {
                // Section 34.9's personal mode, and any organisation that has not built structure
                // yet. Still ledgered: cost on the artifact is section 34.8, not a budget feature.
                var free = await RecordAsync(request, estimate, [], now, cancellationToken).ConfigureAwait(false);

                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return BudgetDecision.Ungoverned(estimate, free.Id);
            }

            var (constraints, alerts) = await MeasureAsync(applicable, estimate, now, cancellationToken)
                .ConfigureAwait(false);

            var decision = await DecideAsync(request, estimate, constraints, alerts, now, cancellationToken)
                .ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return decision;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<LedgerEntry?> SettleAsync(
        Guid ledgerEntryId,
        decimal usd,
        decimal quotaSessions,
        decimal imputedUsd,
        CancellationToken cancellationToken = default)
    {
        var entry = await _db.LedgerEntries
            .FirstOrDefaultAsync(row => row.Id == ledgerEntryId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.State != LedgerState.Reserved)
        {
            // Already settled, already released, or swept. Settling twice would double-charge a
            // budget, and a sweep that beat the settlement is a released hold, not a free session.
            return entry;
        }

        var estimated = entry.Unit == LedgerUnit.Usd ? entry.EstimatedUsd : entry.EstimatedQuotaSessions;

        entry.Settle(usd, quotaSessions, imputedUsd, _clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Settled ledger entry {EntryId}: estimated {Estimated}, actual {Actual} {Unit} "
            + "(section 34.4).",
            entry.Id,
            estimated,
            entry.Amount,
            entry.Unit);

        return entry;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(Guid ledgerEntryId, CancellationToken cancellationToken = default)
    {
        var entry = await _db.LedgerEntries
            .FirstOrDefaultAsync(row => row.Id == ledgerEntryId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.State != LedgerState.Reserved)
        {
            return false;
        }

        entry.Release(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var expired = await _db.LedgerEntries
            .Where(entry => entry.State == LedgerState.Reserved
                && entry.ReservedUntil != null
                && entry.ReservedUntil <= now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in expired)
        {
            entry.Release(now);
        }

        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Released {Count} expired budget reservation(s). A hold that reaches its TTL is an "
                + "orchestrator that died mid-session (section 34.4).",
                expired.Count);
        }

        return expired.Count;
    }

    /// <summary>
    /// Loads the organisation's budgets with <c>FOR UPDATE</c> row locks, ordered by id.
    /// </summary>
    /// <remarks>
    /// The ordering is not cosmetic. Two transactions taking the same locks in different orders
    /// deadlock, and a deadlocked budget check fails a dispatch for a reason nobody can read from
    /// the error. A stable order means concurrent reservations queue behind each other instead.
    /// </remarks>
    private async Task<List<Budget>> LockBudgetsAsync(Guid orgId, CancellationToken cancellationToken)
        => await _db.Budgets
            .FromSql($"SELECT * FROM budgets WHERE org_id = {orgId} ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Works out what each applicable budget has left and what this work would take from it.
    /// </summary>
    private async Task<(List<BudgetConstraint> Constraints, List<BudgetAlert> Alerts)> MeasureAsync(
        List<Budget> applicable,
        BudgetEstimate estimate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var committed = new Dictionary<Guid, decimal>();
        var windows = new Dictionary<Guid, BudgetPeriodWindow>();
        var caps = new Dictionary<Guid, decimal>();

        foreach (var budget in applicable)
        {
            var window = BudgetPeriodWindow.For(budget, now);
            windows[budget.Id] = window;
            committed[budget.Id] = await CommittedAsync(budget, window, now, cancellationToken)
                .ConfigureAwait(false);
            caps[budget.Id] = await CapAsync(budget, window, now, cancellationToken).ConfigureAwait(false);
        }

        // Section 34.3's guaranteed floor. The most specific applicable budget that reserves one, in
        // the currency being spent, exempts spend inside its reserve from every broader budget:
        // Ayesha always has her $50 even when the team pool is drained by other people.
        var reserver = applicable
            .Where(budget => budget.ReservedAmount > 0m && budget.Unit == estimate.Unit)
            .OrderByDescending(budget => BudgetScopeSet.Specificity(budget.ScopeType))
            .FirstOrDefault();

        var exempt = 0m;
        var reserverRank = int.MinValue;

        if (reserver is not null)
        {
            var floorLeft = Math.Max(0m, reserver.ReservedAmount - committed[reserver.Id]);
            exempt = Math.Min(estimate.AmountIn(reserver.Unit), floorLeft);
            reserverRank = BudgetScopeSet.Specificity(reserver.ScopeType);
        }

        var constraints = new List<BudgetConstraint>(applicable.Count);
        var alerts = new List<BudgetAlert>();

        foreach (var budget in applicable)
        {
            var required = estimate.AmountIn(budget.Unit);

            var exempted = exempt > 0m && BudgetScopeSet.Specificity(budget.ScopeType) < reserverRank;
            if (exempted)
            {
                required = Math.Max(0m, required - exempt);
            }

            var constraint = new BudgetConstraint(
                budget.Id,
                budget.Name,
                budget.Unit,
                caps[budget.Id],
                committed[budget.Id],
                required,
                budget.Behaviour,
                windows[budget.Id].End,
                budget.ApprovalThreshold,
                ExemptByReserve: exempted && required == 0m);

            constraints.Add(constraint);

            // Section 34.8: alerts fire on the crossing, not on every session afterwards, so the
            // budget owner gets one message at 90% rather than one per request for the rest of the
            // month.
            var before = constraint.Amount <= 0m ? 1d : (double)(constraint.Committed / constraint.Amount);

            alerts.AddRange(budget.AlertThresholds
                .Where(threshold => before < threshold && constraint.Utilisation >= threshold)
                .Select(threshold => new BudgetAlert(
                    budget.Id,
                    budget.Name,
                    threshold,
                    constraint.Utilisation)));
        }

        return (constraints, alerts);
    }

    /// <summary>
    /// Settled spend plus live reservations inside the budget's current period.
    /// </summary>
    /// <remarks>
    /// A reservation past its TTL is excluded here as well as being swept, so headroom is correct
    /// even when the sweep has not run yet. The sweep tidies the rows; this is what makes the
    /// arithmetic right without it.
    /// </remarks>
    private async Task<decimal> CommittedAsync(
        Budget budget,
        BudgetPeriodWindow window,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entries = _db.LedgerEntries
            .AsNoTracking()
            .Where(entry => entry.OrgId == budget.OrgId
                && entry.BudgetIds.Contains(budget.Id)
                && entry.CreatedAt >= window.Start
                && entry.CreatedAt < window.End
                && (entry.State == LedgerState.Settled
                    || (entry.State == LedgerState.Reserved
                        && (entry.ReservedUntil == null || entry.ReservedUntil > now))));

        return budget.Unit == LedgerUnit.Usd
            ? await entries.SumAsync(entry => entry.Usd, cancellationToken).ConfigureAwait(false)
            : await entries.SumAsync(entry => entry.QuotaSessions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cap for this period, including anything rolled over from the last one (section 34.2).
    /// </summary>
    /// <remarks>
    /// One period back, not a running total. A budget that accumulates every unspent month forever
    /// is not a monthly budget, and <c>rollover_cap</c> exists precisely because unbounded carry is
    /// the thing organisations do not want.
    /// </remarks>
    private async Task<decimal> CapAsync(
        Budget budget,
        BudgetPeriodWindow window,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (budget.Rollover == BudgetRollover.None)
        {
            return budget.Amount;
        }

        var previous = BudgetPeriodWindow.For(budget, window.Start.AddTicks(-1));
        if (previous.Start >= window.Start)
        {
            return budget.Amount;
        }

        var spent = await CommittedAsync(budget, previous, now, cancellationToken).ConfigureAwait(false);
        var unspent = Math.Max(0m, budget.Amount - spent);

        var carried = budget.Rollover == BudgetRollover.Capped
            ? Math.Min(unspent, budget.RolloverCap ?? 0m)
            : unspent;

        return budget.Amount + carried;
    }

    /// <summary>
    /// Turns the arithmetic into section 34.5's behaviour, and takes the hold where one is due.
    /// </summary>
    private async Task<BudgetDecision> DecideAsync(
        BudgetReservationRequest request,
        BudgetEstimate estimate,
        List<BudgetConstraint> constraints,
        IReadOnlyList<BudgetAlert> alerts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var breached = constraints.Where(constraint => !constraint.HasHeadroom).ToList();

        if (breached.Count == 0)
        {
            // Section 34.2's approval_threshold: spend above it needs an approver, below it flows.
            // Not a breach — the money is there — so it is worth a different sentence.
            var overThreshold = constraints
                .Where(constraint => constraint.ApprovalThreshold is { } threshold
                    && constraint.Required > threshold)
                .OrderBy(constraint => constraint.ApprovalThreshold)
                .FirstOrDefault();

            if (overThreshold is not null)
            {
                var who = await _authority.DescribeAsync(request.Scope.OrgId, cancellationToken)
                    .ConfigureAwait(false);

                return new BudgetDecision
                {
                    Outcome = BudgetOutcome.RequiresApproval,
                    Estimate = estimate,
                    Constraints = constraints,
                    Alerts = alerts,
                    Message = BudgetLimitMessage.ForThreshold(
                        overThreshold,
                        overThreshold.ApprovalThreshold!.Value,
                        who),
                };
            }

            var entry = await RecordAsync(request, estimate, constraints, now, cancellationToken)
                .ConfigureAwait(false);

            return new BudgetDecision
            {
                Outcome = BudgetOutcome.Allowed,
                Estimate = estimate,
                Constraints = constraints,
                Alerts = alerts,
                LedgerEntryId = entry.Id,
            };
        }

        // Every breached budget gets a say and the strictest one wins. Anything else lets a warn
        // budget overrule a block one, which is a cap that does not cap.
        var behaviour = breached.Max(constraint => Severity(constraint.Behaviour));
        var deciding = breached
            .Where(constraint => Severity(constraint.Behaviour) == behaviour)
            .OrderBy(constraint => constraint.Headroom)
            .ToList();

        var authority = await _authority.DescribeAsync(request.Scope.OrgId, cancellationToken)
            .ConfigureAwait(false);

        var message = BudgetLimitMessage.ForBreach(deciding[0], authority, estimate);

        if (deciding[0].Behaviour == BudgetBehaviour.Warn)
        {
            // Proceeds (section 34.5). The hold is still taken, so the next session sees this one.
            var entry = await RecordAsync(request, estimate, constraints, now, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "Budget \"{Budget}\" is over its cap and set to warn; the session runs anyway "
                + "(section 34.5).",
                deciding[0].Name);

            return new BudgetDecision
            {
                Outcome = BudgetOutcome.Warned,
                Estimate = estimate,
                Constraints = constraints,
                Alerts = alerts,
                LedgerEntryId = entry.Id,
                Message = message,
            };
        }

        return new BudgetDecision
        {
            Outcome = deciding[0].Behaviour switch
            {
                BudgetBehaviour.RequireApproval => BudgetOutcome.RequiresApproval,
                BudgetBehaviour.DowngradeModel => BudgetOutcome.DowngradeModel,
                BudgetBehaviour.QueueUntilReset => BudgetOutcome.QueuedUntilReset,
                _ => BudgetOutcome.Blocked,
            },
            Estimate = estimate,
            Constraints = constraints,
            Alerts = alerts,
            Message = message,
            RetryAt = deciding[0].Behaviour == BudgetBehaviour.QueueUntilReset
                ? deciding.Min(constraint => constraint.ResetsAt)
                : null,
            DowngradeToModel = deciding[0].Behaviour == BudgetBehaviour.DowngradeModel
                ? _options.DowngradeModel
                : null,
        };
    }

    /// <summary>Writes the hold. Both units are always recorded (sections 20b.5, 34.1).</summary>
    private async Task<LedgerEntry> RecordAsync(
        BudgetReservationRequest request,
        BudgetEstimate estimate,
        IReadOnlyList<BudgetConstraint> constraints,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var budgetIds = constraints.Select(constraint => constraint.BudgetId).ToList();
        var until = now + _options.ReservationTtl;

        var entry = estimate.Unit == LedgerUnit.Usd
            ? LedgerEntry.ReserveUsd(
                request.Scope.OrgId,
                request.Scope.UserId,
                request.Category,
                estimate.Usd,
                budgetIds,
                request.SessionId,
                request.CredentialGrantId,
                until,
                now)
            : LedgerEntry.ReserveQuota(
                request.Scope.OrgId,
                request.Scope.UserId,
                request.Category,
                estimate.QuotaSessions,
                estimate.ImputedUsd,
                budgetIds,
                request.SessionId,
                request.CredentialGrantId,
                until,
                now);

        _db.LedgerEntries.Add(entry);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry;
    }

    /// <summary>How crude a behaviour is. The crudest breached one decides (section 34.5).</summary>
    private static int Severity(BudgetBehaviour behaviour) => behaviour switch
    {
        BudgetBehaviour.Warn => 1,
        BudgetBehaviour.RequireApproval => 2,
        BudgetBehaviour.DowngradeModel => 3,
        BudgetBehaviour.QueueUntilReset => 4,
        _ => 5,
    };
}
