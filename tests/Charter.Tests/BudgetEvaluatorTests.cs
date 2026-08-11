using Charter.Budgets;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 34's reserve-then-settle accounting, against a real Postgres.
/// </summary>
/// <remarks>
/// These run against a database on purpose. The property section 34.4 exists to guarantee — that ten
/// concurrent sessions cannot collectively blow one cap — is a property of row locks in a
/// transaction, and an in-memory double would pass every one of these tests while the shipped code
/// took no locks at all.
/// </remarks>
public class BudgetEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TenConcurrentReservationsCannotCollectivelyExceedTheCap()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Ten dollars, two dollars a session. Five may run; the other five must not, and the
        // arithmetic must hold no matter how the five that lose interleave with the five that win.
        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Concurrency cap",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 10m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        var estimate = Usd(2m);

        // Real concurrency: ten independent contexts, ten connections, started together. Running
        // these one after another would pass against code that took no locks at all, which is the
        // exact failure section 34.4 is written about.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = fixture.NewScope();
            await barrier.Task;

            return await scope.Evaluator.ReserveAsync(
                fixture.Reservation(estimate),
                TestContext.Current.CancellationToken);
        }).ToArray();

        barrier.SetResult();

        var decisions = await Task.WhenAll(attempts);

        Assert.Equal(5, decisions.Count(decision => decision.Permitted));
        Assert.Equal(5, decisions.Count(decision => decision.Outcome == BudgetOutcome.Blocked));

        // And the ledger agrees with the decisions, which is the part that actually matters: a
        // decision that says no while a row says yes is a cap that leaks.
        Assert.Equal(10m, await fixture.CommittedAsync());
    }

    [Fact]
    public async Task ASessionNeedsHeadroomInEveryApplicableBudgetNotTheMostSpecificOne()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 34.3's example, exactly: a user with $200 left inside an exhausted team pool.
        var pool = await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Ops Tooling",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 100m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Ayesha",
            BudgetScopeType.User,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 200m,
            behaviour: BudgetBehaviour.Block,
            scopeId: fixture.UserId.ToString(),
            now: Now));

        await fixture.SpendAsync(pool.Id, 100m);

        var decision = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(1m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Blocked, decision.Outcome);
        Assert.Null(decision.LedgerEntryId);

        // Both budgets are reported, so the card can say which one ran out rather than "a budget".
        Assert.Equal(2, decision.Constraints.Count);
        Assert.Equal("Ops Tooling", Assert.Single(decision.Breached).Name);
    }

    [Fact]
    public async Task AGuaranteedFloorSpendsThroughAnExhaustedPoolAboveIt()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 34.3: "reserved_amount guarantees a floor - Ayesha always has $50 even if the team
        // pool is drained by others. Above her reserve she competes for the shared pool."
        var pool = await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Ops Tooling",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 100m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Ayesha",
            BudgetScopeType.User,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 200m,
            behaviour: BudgetBehaviour.Block,
            scopeId: fixture.UserId.ToString(),
            reservedAmount: 50m,
            now: Now));

        await fixture.SpendAsync(pool.Id, 100m);

        var inside = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(20m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Allowed, inside.Outcome);
        Assert.True(inside.Constraints.Single(c => c.Name == "Ops Tooling").ExemptByReserve);

        // $20 of her $50 floor is gone. $40 is above what is left of it, so the drained pool is
        // consulted again for the excess and refuses.
        await using var second = fixture.NewScope();

        var above = await second.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(40m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Blocked, above.Outcome);
    }

    [Fact]
    public async Task AReservationStopsCountingOnceItsTtlHasPassedAndTheSweepReleasesIt()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Stranded",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 5m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        var first = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(5m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Allowed, first.Outcome);

        // The orchestrator that took that hold has died. Nothing settles it and nothing releases it.
        await using var blocked = fixture.NewScope();
        Assert.Equal(
            BudgetOutcome.Blocked,
            (await blocked.Evaluator.ReserveAsync(
                fixture.Reservation(Usd(5m)),
                TestContext.Current.CancellationToken)).Outcome);

        fixture.Clock.Advance(TimeSpan.FromHours(3));

        // Headroom is right before the sweep runs, because an expired hold stops counting on its own.
        await using var afterTtl = fixture.NewScope();
        Assert.Equal(
            BudgetOutcome.Allowed,
            (await afterTtl.Evaluator.ReserveAsync(
                fixture.Reservation(Usd(5m)),
                TestContext.Current.CancellationToken)).Outcome);

        // And the sweep tidies the abandoned row rather than leaving a reservation that never
        // resolved.
        // At least one, not exactly one: the sweep is instance-wide (an instance serves exactly one
        // organisation, section 7.2a) and the throwaway database is shared with every other test.
        await using var sweeper = fixture.NewScope();
        Assert.True(await sweeper.Evaluator.SweepExpiredAsync(TestContext.Current.CancellationToken) >= 1);

        await using var reader = fixture.NewContext();
        var swept = await reader.LedgerEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == first.LedgerEntryId, TestContext.Current.CancellationToken);

        Assert.Equal(LedgerState.Released, swept.State);
        Assert.Equal(0m, swept.Usd);

        // What was predicted survives the release, because that is the record section 34.4 wants to
        // learn from.
        Assert.Equal(5m, swept.EstimatedUsd);
    }

    [Fact]
    public async Task SettlingReleasesTheDifferenceBetweenTheEstimateAndTheActual()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Settlement",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 10m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        var reserved = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(8m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(8m, await fixture.CommittedAsync());

        await using var settling = fixture.NewScope();
        var settled = await settling.Evaluator.SettleAsync(
            reserved.LedgerEntryId!.Value,
            usd: 1.25m,
            quotaSessions: 0m,
            imputedUsd: 1.25m,
            TestContext.Current.CancellationToken);

        Assert.NotNull(settled);
        Assert.Equal(LedgerState.Settled, settled.State);

        // $6.75 of hold went back to the budget the moment the work finished.
        Assert.Equal(1.25m, await fixture.CommittedAsync());

        // Actual versus estimate is kept so the estimator can be graded (section 34.4).
        Assert.Equal(8m, settled.EstimatedUsd);
        Assert.Equal(-6.75m, settled.EstimateError);

        await using var after = fixture.NewScope();
        Assert.Equal(
            BudgetOutcome.Allowed,
            (await after.Evaluator.ReserveAsync(
                fixture.Reservation(Usd(8m)),
                TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task CancellingReleasesTheWholeHold()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Cancelled",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 4m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        var reserved = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(4m)),
            TestContext.Current.CancellationToken);

        await using var releasing = fixture.NewScope();
        Assert.True(await releasing.Evaluator.ReleaseAsync(
            reserved.LedgerEntryId!.Value,
            TestContext.Current.CancellationToken));

        Assert.Equal(0m, await fixture.CommittedAsync());

        // Releasing twice is not an error and does not credit the budget twice.
        await using var again = fixture.NewScope();
        Assert.False(await again.Evaluator.ReleaseAsync(
            reserved.LedgerEntryId!.Value,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASettledEntryIsNeverSettledTwice()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var reserved = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(1m)),
            TestContext.Current.CancellationToken);

        await using var first = fixture.NewScope();
        await first.Evaluator.SettleAsync(
            reserved.LedgerEntryId!.Value,
            1m,
            0m,
            1m,
            TestContext.Current.CancellationToken);

        // A retried job settling the same entry again would double-charge every budget it names.
        await using var second = fixture.NewScope();
        var again = await second.Evaluator.SettleAsync(
            reserved.LedgerEntryId!.Value,
            99m,
            0m,
            99m,
            TestContext.Current.CancellationToken);

        Assert.Equal(1m, again!.Usd);
    }

    [Fact]
    public async Task PersonalModeIsUngovernedAndStillLedgered()
    {
        await using var fixture = await BudgetFixture.CreateAsync(OrganizationMode.Personal);
        if (fixture is null)
        {
            return;
        }

        // Section 34.9: personal mode has no budgets at all. That is expressed as an organisation
        // with no budget rows, not as a branch that skips the check (section 7.2).
        Assert.Null(BudgetDefaults.For(fixture.Organization, new BudgetOptions()));
        Assert.Empty(await fixture.Scope.Db.Budgets
            .Where(budget => budget.OrgId == fixture.OrgId)
            .ToListAsync(TestContext.Current.CancellationToken));

        var decision = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(4_000m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Allowed, decision.Outcome);
        Assert.Empty(decision.Constraints);
        Assert.Equal(string.Empty, decision.Message);

        // Still ledgered: section 34.8 shows cost on the artifact whether or not anything capped it.
        Assert.NotNull(decision.LedgerEntryId);

        await using var reader = fixture.NewContext();
        var entry = await reader.LedgerEntries
            .AsNoTracking()
            .SingleAsync(row => row.Id == decision.LedgerEntryId, TestContext.Current.CancellationToken);

        Assert.Empty(entry.BudgetIds);
        Assert.Equal(4_000m, entry.Usd);
    }

    [Fact]
    public async Task AnOrganisationStartsWithOneBudgetThatAsksRatherThanRefuses()
    {
        await using var fixture = await BudgetFixture.CreateAsync(OrganizationMode.Organization);
        if (fixture is null)
        {
            return;
        }

        // Section 34.9: org-level require_approval above a modest per-session threshold, and no
        // per-user budgets until an admin adds them.
        var seeded = BudgetDefaults.For(fixture.Organization, new BudgetOptions());

        Assert.NotNull(seeded);
        Assert.Equal(BudgetBehaviour.RequireApproval, seeded.Behaviour);
        Assert.Equal(BudgetScopeType.Org, seeded.ScopeType);
        Assert.Equal(5m, seeded.ApprovalThreshold);

        // Section 34.6: chat is not singled out by the shipped default. Rationing the cheapest way
        // to resolve a request pushes people straight to building.
        Assert.Empty(seeded.Categories);

        await fixture.AddBudgetAsync(seeded);

        // Under the threshold it simply runs.
        var small = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(1m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Allowed, small.Outcome);

        // Over it, the work does not stop — it acquires a human decision (section 34.5).
        await using var larger = fixture.NewScope();
        var big = await larger.Evaluator.ReserveAsync(
            fixture.Reservation(Usd(40m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.RequiresApproval, big.Outcome);
        Assert.Null(big.LedgerEntryId);
        Assert.Contains("There is budget for it.", big.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.AdminEmail, big.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubscriptionSessionSpendsQuotaAndNotDollars()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 34.1 and 20b.5: never conflate the two currencies. A dollar cap must not be spent
        // by work that costs no dollars, and the quota it does consume must still be governed.
        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Dollars",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 1m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        await fixture.AddBudgetAsync(Budget.Create(
            fixture.OrgId,
            "Seats",
            BudgetScopeType.Org,
            LedgerUnit.QuotaSessions,
            BudgetPeriod.Daily,
            amount: 1m,
            behaviour: BudgetBehaviour.Block,
            now: Now));

        var subscription = new BudgetEstimate
        {
            Unit = LedgerUnit.QuotaSessions,
            QuotaSessions = 1m,
            ImputedUsd = 12m,
            Basis = BudgetEstimateBasis.Priced,
        };

        var first = await fixture.Scope.Evaluator.ReserveAsync(
            fixture.Reservation(subscription),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Allowed, first.Outcome);

        // $12 of work went through a $1 dollar cap, because it cost no dollars. The imputed figure
        // is on the row so the dashboard does not report it as $0.00.
        await using var reader = fixture.NewContext();
        var entry = await reader.LedgerEntries
            .AsNoTracking()
            .SingleAsync(row => row.Id == first.LedgerEntryId, TestContext.Current.CancellationToken);

        Assert.Equal(0m, entry.Usd);
        Assert.Equal(1m, entry.QuotaSessions);
        Assert.Equal(12m, entry.ImputedUsd);

        // The quota cap is the one that governs it, and it is now spent.
        await using var second = fixture.NewScope();
        var next = await second.Evaluator.ReserveAsync(
            fixture.Reservation(subscription),
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetOutcome.Blocked, next.Outcome);
        Assert.Contains("1 session", next.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("$", next.Message, StringComparison.Ordinal);
    }

    private static BudgetEstimate Usd(decimal amount) => new()
    {
        Unit = LedgerUnit.Usd,
        Usd = amount,
        ImputedUsd = amount,
        Basis = BudgetEstimateBasis.Priced,
    };
}

/// <summary>One organisation, one admin, one repo, and a clock the test drives.</summary>
internal sealed class BudgetFixture : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private readonly string _connectionString;
    private readonly List<BudgetScope> _scopes = [];

    private BudgetFixture(string connectionString, Organization organization, Guid userId, Guid repoId, string adminEmail)
    {
        _connectionString = connectionString;
        Organization = organization;
        UserId = userId;
        RepoId = repoId;
        AdminEmail = adminEmail;
        Clock = new TestClock(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        Scope = NewScope();
    }

    public Organization Organization { get; }

    public Guid OrgId => Organization.Id;

    public Guid UserId { get; }

    public Guid RepoId { get; }

    public string AdminEmail { get; }

    public TestClock Clock { get; }

    /// <summary>A scope the test can use directly without opening one.</summary>
    public BudgetScope Scope { get; }

    /// <summary>Returns null - and the caller returns green - when no test database is configured.</summary>
    public static async Task<BudgetFixture?> CreateAsync(OrganizationMode mode = OrganizationMode.Organization)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the budget tests.");
            return null;
        }

        var connectionString = DatabaseUrl.ToNpgsql(url);

        await using var db = NewContext(connectionString);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tag = Guid.CreateVersion7().ToString("N");
        var organization = Organization.Create($"budget-tests-{tag}", mode);
        var admin = User.Create($"admin-{tag}@charter.invalid", "Ayesha Khan");
        var member = Member.Create(organization.Id, admin.Id, Member.AllRoles);
        var repo = Repo.Connect(organization.Id, 909, $"charter/budgets-{tag}");

        db.Organizations.Add(organization);
        db.Users.Add(admin);
        db.Members.Add(member);
        db.Repos.Add(repo);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new BudgetFixture(connectionString, organization, admin.Id, repo.Id, admin.Email);
    }

    public BudgetScope NewScope()
    {
        var scope = new BudgetScope(NewContext(_connectionString), Clock);
        _scopes.Add(scope);

        return scope;
    }

    public CharterDbContext NewContext() => NewContext(_connectionString);

    public async Task<Budget> AddBudgetAsync(Budget budget)
    {
        await using var db = NewContext(_connectionString);

        db.Budgets.Add(budget);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return budget;
    }

    /// <summary>Books settled spend against a budget, as a finished session would have.</summary>
    public async Task SpendAsync(Guid budgetId, decimal usd)
    {
        await using var db = NewContext(_connectionString);

        var entry = LedgerEntry.ReserveUsd(
            OrgId,
            UserId,
            LedgerCategory.Build,
            usd,
            [budgetId],
            now: Clock.GetUtcNow());

        entry.Settle(usd, 0m, usd, Clock.GetUtcNow());

        db.LedgerEntries.Add(entry);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Everything committed against this organisation, in dollars.</summary>
    public async Task<decimal> CommittedAsync()
    {
        await using var db = NewContext(_connectionString);

        return await db.LedgerEntries
            .AsNoTracking()
            .Where(entry => entry.OrgId == OrgId && entry.State != LedgerState.Released)
            .SumAsync(entry => entry.Usd, TestContext.Current.CancellationToken);
    }

    public BudgetReservationRequest Reservation(BudgetEstimate estimate) => new()
    {
        Scope = new BudgetScopeSet
        {
            OrgId = OrgId,
            UserId = UserId,
            RepoId = RepoId,
            Roles = Member.AllRoles,
        },
        Category = LedgerCategory.Build,
        Model = "anthropic/claude-opus-5",
        Estimate = estimate,
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }
    }

    private static CharterDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, connectionString);

        return new CharterDbContext(options.Options);
    }
}

/// <summary>One evaluator over one connection, which is what a request scope is.</summary>
internal sealed class BudgetScope : IAsyncDisposable
{
    public BudgetScope(CharterDbContext db, TimeProvider clock)
    {
        Db = db;

        var options = new BudgetOptions();

        Evaluator = new BudgetEvaluator(
            db,
            new BudgetEstimator(db, new StaticModelPriceTable(), options),
            new BudgetAuthority(db),
            options,
            clock,
            NullLogger<BudgetEvaluator>.Instance);
    }

    public CharterDbContext Db { get; }

    public IBudgetEvaluator Evaluator { get; }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
