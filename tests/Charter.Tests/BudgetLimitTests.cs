using Charter.Budgets;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 34.5's five behaviours at the limit, and the rule that governs all of them.
/// </summary>
/// <remarks>
/// <em>Every limit message names who can raise it. A dead end that doesn't say who to ask is the
/// fastest way to make people stop using the tool.</em> That is a property of every message, so it
/// is tested as one rather than checked once on the one behaviour somebody happened to write a test
/// for.
/// </remarks>
public class BudgetLimitMessageTests
{
    private static readonly DateTimeOffset Resets = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly BudgetAuthorityDescription Named =
        BudgetAuthority.Compose(["Ayesha Khan (ayesha@charter.invalid)"]);

    public static TheoryData<BudgetBehaviour> EveryBehaviour =>
    [
        BudgetBehaviour.Warn,
        BudgetBehaviour.RequireApproval,
        BudgetBehaviour.DowngradeModel,
        BudgetBehaviour.QueueUntilReset,
        BudgetBehaviour.Block,
    ];

    [Theory]
    [MemberData(nameof(EveryBehaviour))]
    public void EveryLimitMessageNamesSomebodyWhoCanRaiseIt(BudgetBehaviour behaviour)
    {
        var message = BudgetLimitMessage.ForBreach(Constraint(behaviour), Named);

        Assert.EndsWith("Ask Ayesha Khan (ayesha@charter.invalid) to raise it.", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryBehaviour))]
    public void EveryLimitMessageStillNamesARoleWhenNobodyCanBeNamed(BudgetBehaviour behaviour)
    {
        // An instance with no admin left is a real state — the last one was removed — and the
        // message must still say who to ask rather than trailing off.
        var message = BudgetLimitMessage.ForBreach(Constraint(behaviour), BudgetAuthority.Compose([]));

        Assert.EndsWith(BudgetAuthority.RoleFallback, message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryBehaviour))]
    public void EveryLimitMessageCarriesTheBudgetsNameAndItsFigures(BudgetBehaviour behaviour)
    {
        var message = BudgetLimitMessage.ForBreach(Constraint(behaviour), Named);

        Assert.Contains("Ops Tooling", message, StringComparison.Ordinal);
        Assert.Contains("$", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueuedSessionIsToldTheDateItsBudgetResetsAndNeverAnEta()
    {
        var message = BudgetLimitMessage.ForBreach(Constraint(BudgetBehaviour.QueueUntilReset), Named);

        Assert.Contains("1 September 2026", message, StringComparison.Ordinal);

        // Section 6: elapsed time only, never an estimate of when the work will be done. A reset
        // date is a fact about the budget; "about two hours" would be an ETA.
        Assert.DoesNotContain("estimated", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADowngradeSaysTheSessionIsLabelledRatherThanSilentlyCheapened()
    {
        var message = BudgetLimitMessage.ForBreach(Constraint(BudgetBehaviour.DowngradeModel), Named);

        Assert.Contains("cheaper model", message, StringComparison.Ordinal);
        Assert.Contains("labelled", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWarningSaysTheWorkIsRunningAnyway()
    {
        var message = BudgetLimitMessage.ForBreach(Constraint(BudgetBehaviour.Warn), Named);

        Assert.Contains("running anyway", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpricedModelIsNamedAsUnpricedRatherThanReportedAsFree()
    {
        var message = BudgetLimitMessage.ForBreach(
            Constraint(BudgetBehaviour.Block),
            Named,
            BudgetEstimate.Free);

        Assert.Contains("knows what that model costs", message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaIsNeverPrintedAsMoney()
    {
        // Section 34.1: never conflate the two currencies. "$1.00" for one subscription session is
        // the exact lie section 20b.5 says makes dashboards useless.
        Assert.Equal("1 session", BudgetLimitMessage.Amount(1m, LedgerUnit.QuotaSessions));
        Assert.Equal("2.5 sessions", BudgetLimitMessage.Amount(2.5m, LedgerUnit.QuotaSessions));
        Assert.Equal("$1.00", BudgetLimitMessage.Amount(1m, LedgerUnit.Usd));
    }

    [Fact]
    public void MoreThanThreeAdminsStopsListingThem()
    {
        var many = BudgetAuthority.Compose(["A", "B", "C"]);

        Assert.True(many.Named);
        Assert.Equal("Ask A, B or C to raise it.", many.Sentence);
    }

    private static BudgetConstraint Constraint(BudgetBehaviour behaviour) => new(
        Guid.CreateVersion7(),
        "Ops Tooling",
        LedgerUnit.Usd,
        Amount: 1_500m,
        Committed: 1_498m,
        Required: 3.20m,
        behaviour,
        Resets);
}

/// <summary>The period arithmetic every budget's headroom is measured over (section 34.2).</summary>
public class BudgetPeriodTests
{
    private static readonly DateTimeOffset Mid = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMonthlyBudgetRunsFromTheFirstWhenNothingAnchorsIt()
    {
        var window = BudgetPeriodWindow.For(Monthly(anchor: null), Mid);

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), window.End);
    }

    [Fact]
    public void AMonthlyBudgetFollowsItsBillingDay()
    {
        var window = BudgetPeriodWindow.For(
            Monthly(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            Mid);

        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), window.End);
    }

    [Fact]
    public void ARollingThirtyDayBudgetLooksBackwardsFromNow()
    {
        var budget = Budget.Create(
            Guid.CreateVersion7(),
            "Rolling",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Rolling30Days,
            100m);

        var window = BudgetPeriodWindow.For(budget, Mid);

        Assert.Equal(Mid.AddDays(-30), window.Start);
        Assert.True(window.Contains(Mid));
    }

    [Fact]
    public void ACampaignBudgetIsItsOwnWindow()
    {
        // Section 34.7: one_off with starts_at/ends_at, for a push that should not distort the
        // recurring baseline.
        var starts = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var ends = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

        var budget = Budget.Create(
            Guid.CreateVersion7(),
            "Migration push",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.OneOff,
            2_000m,
            startsAt: starts,
            endsAt: ends);

        var window = BudgetPeriodWindow.For(budget, Mid);

        Assert.Equal(starts, window.Start);
        Assert.Equal(ends, window.End);

        // And it stops governing anything once it is over.
        Assert.False(budget.IsActiveAt(ends.AddDays(1)));
    }

    private static Budget Monthly(DateTimeOffset? anchor) => Budget.Create(
        Guid.CreateVersion7(),
        "Monthly",
        BudgetScopeType.Org,
        LedgerUnit.Usd,
        BudgetPeriod.Monthly,
        100m,
        periodAnchor: anchor);
}

/// <summary>Which budgets govern a piece of work (section 34.3).</summary>
public class BudgetScopeTests
{
    private static readonly Guid OrgId = Guid.CreateVersion7();

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ABudgetForAnotherRepositoryDoesNotApply()
    {
        var scope = Scope() with { RepoId = Guid.CreateVersion7() };

        var budget = Budget.Create(
            OrgId,
            "Other repo",
            BudgetScopeType.Repo,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            50m,
            scopeId: Guid.CreateVersion7().ToString());

        Assert.False(scope.IsGovernedBy(budget, LedgerCategory.Build, Now));
    }

    [Fact]
    public void ACategoryScopedBudgetOnlyGovernsThatCategory()
    {
        // Section 34.6: teaching is its own line, because it is the first thing an admin cuts when
        // it is bundled with build spend.
        var budget = Budget.Create(
            OrgId,
            "Teaching",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            50m,
            categories: [LedgerCategory.Teach]);

        Assert.True(Scope().IsGovernedBy(budget, LedgerCategory.Teach, Now));
        Assert.False(Scope().IsGovernedBy(budget, LedgerCategory.Build, Now));
    }

    [Fact]
    public void ARoleScopedBudgetMatchesAnyRoleTheMemberHolds()
    {
        var budget = Budget.Create(
            OrgId,
            "Requesters",
            BudgetScopeType.Role,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            50m,
            scopeId: nameof(MemberRole.Requester));

        Assert.True(Scope().IsGovernedBy(budget, LedgerCategory.Build, Now));

        var engineerOnly = Scope() with { Roles = [MemberRole.Engineer] };
        Assert.False(engineerOnly.IsGovernedBy(budget, LedgerCategory.Build, Now));
    }

    [Fact]
    public void SpecificityRunsFromTheOrganisationOutwardsToOnePerson()
    {
        // Only reserved_amount depends on this order; spending never picks a winner (section 34.3).
        Assert.True(
            BudgetScopeSet.Specificity(BudgetScopeType.User)
            > BudgetScopeSet.Specificity(BudgetScopeType.Repo));

        Assert.True(
            BudgetScopeSet.Specificity(BudgetScopeType.Repo)
            > BudgetScopeSet.Specificity(BudgetScopeType.Org));
    }

    private static BudgetScopeSet Scope() => new()
    {
        OrgId = OrgId,
        UserId = Guid.CreateVersion7(),
        Roles = [MemberRole.Requester],
    };
}
