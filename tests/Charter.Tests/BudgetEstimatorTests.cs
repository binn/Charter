using Charter.Budgets;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Section 34.4 step 1: estimate before dispatch.
/// </summary>
/// <remarks>
/// The estimator is deliberately coarse and these tests say so — what they check is that it is
/// coarse in defensible directions: bigger specs cost more, an unpriced model reports as unknown
/// rather than as free, subscription work is denominated in quota, and history replaces the
/// heuristic once there is enough of it.
/// </remarks>
public class BudgetEstimatorTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public void ABiggerSpecEstimatesHigherThanASmallerOne()
    {
        var small = BudgetEstimator.ScopeFactor(Request(specCharacters: 500, criteria: 1));
        var typical = BudgetEstimator.ScopeFactor(Request(specCharacters: 4_000, criteria: 3));
        var large = BudgetEstimator.ScopeFactor(Request(specCharacters: 40_000, criteria: 12));

        Assert.True(small < typical);
        Assert.True(typical < large);
        Assert.Equal(1m, typical);
    }

    [Fact]
    public void TheScopeFactorIsClampedAtBothEnds()
    {
        // Two inputs available before dispatch correlate with cost loosely. Unclamped they produce
        // confident nonsense: a 400,000-character spec is not eighteen times a build.
        Assert.Equal(0.5m, BudgetEstimator.ScopeFactor(Request(specCharacters: 0, criteria: 0) with
        {
            AcceptanceCriteria = 0,
            SpecBodyMd = string.Empty.PadRight(1),
        }));

        Assert.Equal(3m, BudgetEstimator.ScopeFactor(Request(specCharacters: 400_000, criteria: 90)));
    }

    [Fact]
    public void AnEmptySpecIsAnOrdinaryPieceOfWorkRatherThanAFreeOne()
    {
        // Chat and recon arrive with no spec at all. Treating that as "nothing to do" would let the
        // cheapest thing to estimate be the thing that escapes every budget.
        Assert.Equal(1m, BudgetEstimator.ScopeFactor(new BudgetEstimateRequest
        {
            OrgId = Guid.CreateVersion7(),
            Category = LedgerCategory.Chat,
            Model = "anthropic/claude-opus-5",
        }));
    }

    [Fact]
    public async Task AKnownModelIsPricedFromItsPublishedRates()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var estimate = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId },
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetEstimateBasis.Priced, estimate.Basis);
        Assert.Equal(LedgerUnit.Usd, estimate.Unit);

        // 120k in at $5/M and 30k out at $25/M for a build on Opus. The figure is a hold, not a
        // quote — what matters is that it is the right order of magnitude and not zero.
        Assert.Equal(1.35m, estimate.Usd);
        Assert.Equal(estimate.Usd, estimate.ImputedUsd);
        Assert.Equal(0m, estimate.QuotaSessions);
    }

    [Fact]
    public async Task ACheaperCategoryEstimatesFarBelowABuild()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 34.6: chat is by far the cheapest way to resolve a request, and a budget that
        // priced it like a build would ration the thing that saves whole builds.
        var build = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId },
            TestContext.Current.CancellationToken);

        var chat = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId, Category = LedgerCategory.Chat },
            TestContext.Current.CancellationToken);

        Assert.True(chat.Usd * 10 < build.Usd);
    }

    [Fact]
    public async Task AnUnpricedModelReportsAsUnknownAndNeverAsFree()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var estimate = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with
            {
                OrgId = fixture.OrgId,
                Model = "openai-compatible/llama-3-70b-on-my-own-box",
            },
            TestContext.Current.CancellationToken);

        // Zero with Unpriced is the honest answer and it is also a hole: a budget cannot govern
        // spend nobody can price. BudgetLimitMessage says so rather than letting it look free.
        Assert.Equal(BudgetEstimateBasis.Unpriced, estimate.Basis);
        Assert.Equal(0m, estimate.Usd);
    }

    [Fact]
    public async Task SubscriptionWorkIsDenominatedInQuotaWithAnImputedDollarFigure()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var estimate = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId, SubscriptionBacked = true },
            TestContext.Current.CancellationToken);

        // Section 20b.5: reporting a subscription session as $0.00 makes budget dashboards lie.
        Assert.Equal(LedgerUnit.QuotaSessions, estimate.Unit);
        Assert.Equal(1m, estimate.QuotaSessions);
        Assert.Equal(0m, estimate.Usd);
        Assert.True(estimate.ImputedUsd > 0m);
    }

    [Fact]
    public async Task EnoughSettledSessionsReplaceTheHeuristicWithWhatThingsActuallyCost()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Two samples is not a distribution.
        await fixture.RecordActualAsync(9m);
        await fixture.RecordActualAsync(9m);

        var tooFew = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId, RepoId = fixture.RepoId },
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetEstimateBasis.Priced, tooFew.Basis);

        // A third one is, and it moves the estimate onto what this repository really costs.
        await fixture.RecordActualAsync(9m);

        var learned = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId, RepoId = fixture.RepoId },
            TestContext.Current.CancellationToken);

        Assert.Equal(BudgetEstimateBasis.Historical, learned.Basis);
        Assert.Equal(3, learned.SampleSize);
        Assert.Equal(9m, learned.Usd);
    }

    [Fact]
    public async Task OneRunawaySessionDoesNotMoveEveryLaterEstimate()
    {
        await using var fixture = await EstimatorFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.RecordActualAsync(1m);
        await fixture.RecordActualAsync(1m);
        await fixture.RecordActualAsync(1m);
        await fixture.RecordActualAsync(400m);

        var estimate = await fixture.Estimator.EstimateAsync(
            Request(4_000, 3) with { OrgId = fixture.OrgId, RepoId = fixture.RepoId },
            TestContext.Current.CancellationToken);

        // The median, not the mean. A mean would have reserved $100 a session for the rest of the
        // month because one build went wrong once.
        Assert.Equal(1m, estimate.Usd);
    }

    private static BudgetEstimateRequest Request(int specCharacters, int criteria) => new()
    {
        OrgId = Guid.CreateVersion7(),
        Category = LedgerCategory.Build,
        Model = "anthropic/claude-opus-5",
        SpecBodyMd = new string('x', specCharacters),
        AcceptanceCriteria = criteria,
    };

    private sealed class EstimatorFixture : IAsyncDisposable
    {
        private readonly CharterDbContext _db;

        private EstimatorFixture(CharterDbContext db, Guid orgId, Guid userId, Guid repoId)
        {
            _db = db;
            OrgId = orgId;
            UserId = userId;
            RepoId = repoId;

            Estimator = new BudgetEstimator(db, new StaticModelPriceTable(), new BudgetOptions());
        }

        public Guid OrgId { get; }

        public Guid UserId { get; }

        public Guid RepoId { get; }

        public IBudgetEstimator Estimator { get; }

        public static async Task<EstimatorFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the estimator tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create($"estimator-{tag}");
            var user = User.Create($"requester-{tag}@charter.invalid", "Requester");
            var repo = Repo.Connect(organization.Id, 707, $"charter/estimator-{tag}");

            db.Organizations.Add(organization);
            db.Users.Add(user);
            db.Repos.Add(repo);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return new EstimatorFixture(db, organization.Id, user.Id, repo.Id);
        }

        /// <summary>A finished build in this repository that really cost this much.</summary>
        public async Task RecordActualAsync(decimal usd)
        {
            var request = Domain.Request.File(OrgId, RepoId, UserId, "The totals are wrong past ten lines.");
            var spec = Spec.Draft(request.Id, 1, "Fix the totals", "Totals are right", "body", "[]");
            var session = Session.Queue(spec.Id, RunnerKind.Agent, "anthropic/claude-opus-5");

            var entry = LedgerEntry.ReserveUsd(OrgId, UserId, LedgerCategory.Build, usd, sessionId: session.Id);
            entry.Settle(usd, 0m, usd);

            _db.Requests.Add(request);
            _db.Specs.Add(spec);
            _db.Sessions.Add(session);
            _db.LedgerEntries.Add(entry);

            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}
