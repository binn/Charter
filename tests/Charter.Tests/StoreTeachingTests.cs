using Charter.Data.Teaching;
using Charter.Domain;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 13's three stores against a real Postgres.
/// </summary>
/// <remarks>
/// These run only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway database and skip
/// otherwise. What they check is the part that has no in-memory equivalent: that a ledger survives
/// the process, that the injection window comes back in the order section 13 needs it in, and that a
/// daily cap rolls over on a day boundary rather than on a restart.
/// </remarks>
public class StoreTeachingTests
{
    [Fact]
    public async Task AConceptLedgerSurvivesTheProcessAndComesBackMostRecentlyReferencedFirst()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var store = new EfConceptLedgerStore(fixture.Scopes, fixture.Clock);

        await store.RecordAsync(
            fixture.UserId,
            ["migration", "Pull request", "  environment variable  "],
            TestContext.Current.CancellationToken);

        // A later pass references one of them again. Section 13 orders the capped injection window on
        // exactly this, so a concept touched today must outrank one touched last week.
        fixture.Clock.Now = fixture.Clock.Now.AddDays(3);
        await store.RecordAsync(fixture.UserId, ["MIGRATION"], TestContext.Current.CancellationToken);

        // A different store instance, as a restarted container would have.
        var reopened = new EfConceptLedgerStore(fixture.Scopes, fixture.Clock);
        var ledger = await reopened.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Equal(3, ledger.Count);
        Assert.Equal("migration", ledger[0].Concept);

        // Normalised on the way in, so "Pull request" and "pull request" are one concept.
        Assert.Contains("pull request", ledger.Select(entry => entry.Concept));
        Assert.Contains("environment variable", ledger.Select(entry => entry.Concept));

        var migration = ledger[0];
        Assert.Equal(2, migration.TimesReferenced);

        // first_explained_at is only ever written by the insert arm: it still means the first time.
        Assert.True(migration.LastReferencedAt > migration.FirstExplainedAt);

        // And the snapshot that goes into the prompt reads the same order.
        var snapshot = ConceptLedgerSnapshot.From(ledger, limit: 2);
        Assert.Equal(3, snapshot.TotalKnown);
        Assert.Equal("migration", snapshot.Concepts[0]);
        Assert.True(snapshot.Knows("Migration"));
    }

    [Fact]
    public async Task RecordingTheSameConceptTwiceInOnePassCountsItOnce()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var store = new EfConceptLedgerStore(fixture.Scopes, fixture.Clock);

        // One teaching pass mentioning a term three ways. Postgres also refuses an ON CONFLICT update
        // that touches the same row twice in one command, so this is both a correctness and a
        // does-it-throw assertion.
        await store.RecordAsync(
            fixture.UserId,
            ["migration", "Migration", " MIGRATION "],
            TestContext.Current.CancellationToken);

        var ledger = await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger);
        Assert.Equal(1, entry.TimesReferenced);
    }

    [Fact]
    public async Task ResettingALedgerClearsOnePersonAndNobodyElse()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var colleague = await fixture.AddUserAsync("colleague");
        var store = new EfConceptLedgerStore(fixture.Scopes, fixture.Clock);

        await store.RecordAsync(fixture.UserId, ["migration"], TestContext.Current.CancellationToken);
        await store.RecordAsync(colleague, ["migration"], TestContext.Current.CancellationToken);

        // Section 13, in as many words: let them reset the ledger, because people forget.
        await store.ResetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Single(await store.GetAsync(colleague, TestContext.Current.CancellationToken));

        // And a reset is a starting-over, not a tombstone: the concept can be learned again.
        await store.RecordAsync(fixture.UserId, ["migration"], TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken)).TimesReferenced);
    }

    [Fact]
    public async Task AGeneratedWalkthroughIsFoundAgainByAnotherInstanceAndCostsNothing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var sessionId = await fixture.AddSessionAsync();
        var store = new EfWalkthroughStore(fixture.Scopes, NullLogger<EfWalkthroughStore>.Instance);

        Assert.Null(await store.FindAsync(
            sessionId,
            TeachingLevel.ExplainEverything,
            TestContext.Current.CancellationToken));

        await store.SaveAsync(
            Walkthrough.Generate(
                sessionId,
                TeachingLevel.ExplainEverything,
                "Your quote wizard stores the selected vertical in a table called Quotes.",
                0.0412m,
                fixture.Clock.GetUtcNow()),
            TestContext.Current.CancellationToken);

        var reopened = new EfWalkthroughStore(fixture.Scopes, NullLogger<EfWalkthroughStore>.Instance);
        var hit = await reopened.FindAsync(
            sessionId,
            TeachingLevel.ExplainEverything,
            TestContext.Current.CancellationToken);

        Assert.NotNull(hit);
        Assert.StartsWith("Your quote wizard", hit.BodyMd, StringComparison.Ordinal);
        Assert.Equal(0.0412m, hit.CostUsd);

        // Section 13's always-visible "more detail" writes at another level rather than over this
        // one, so the rendering somebody already read is still there.
        Assert.Null(await store.FindAsync(
            sessionId,
            TeachingLevel.JustTheDecisions,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegeneratingAtTheSameLevelReplacesTheRenderingRatherThanFailing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var sessionId = await fixture.AddSessionAsync();
        var store = new EfWalkthroughStore(fixture.Scopes, NullLogger<EfWalkthroughStore>.Instance);

        await store.SaveAsync(
            Walkthrough.Generate(sessionId, TeachingLevel.SkipTheBasics, "first", 0.01m, fixture.Clock.GetUtcNow()),
            TestContext.Current.CancellationToken);

        await store.SaveAsync(
            Walkthrough.Generate(sessionId, TeachingLevel.SkipTheBasics, "second", 0.02m, fixture.Clock.GetUtcNow()),
            TestContext.Current.CancellationToken);

        var stored = await store.FindAsync(
            sessionId,
            TeachingLevel.SkipTheBasics,
            TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal("second", stored.BodyMd);

        var rows = await fixture.WithContextAsync(db => db.Walkthroughs
            .CountAsync(walkthrough => walkthrough.SessionId == sessionId, TestContext.Current.CancellationToken));

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task TheExplainThisCapIsSpentPerPersonAndResetsOnTheDayBoundary()
    {
        await using var fixture = await StoreFixture.CreateAsync(
            new DateTimeOffset(2026, 8, 10, 23, 30, 0, TimeSpan.Zero));

        if (fixture is null)
        {
            return;
        }

        var colleague = await fixture.AddUserAsync("colleague");
        var quota = new EfExplainThisQuota(fixture.Scopes, fixture.Clock);

        var first = await quota.TryConsumeAsync(fixture.UserId, 3, TestContext.Current.CancellationToken);
        Assert.True(first.Allowed);
        Assert.Equal(1, first.Used);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero), first.ResetsAt);

        _ = await quota.TryConsumeAsync(fixture.UserId, 3, TestContext.Current.CancellationToken);
        var third = await quota.TryConsumeAsync(fixture.UserId, 3, TestContext.Current.CancellationToken);
        Assert.True(third.Allowed);

        var fourth = await quota.TryConsumeAsync(fixture.UserId, 3, TestContext.Current.CancellationToken);
        Assert.False(fourth.Allowed);

        // Reported capped, so the UI says "3 of 3" rather than "4 of 3" to somebody who kept clicking.
        Assert.Equal(3, fourth.Used);

        // The cap is per user. One curious reader must not silence another.
        Assert.True((await quota.TryConsumeAsync(colleague, 3, TestContext.Current.CancellationToken)).Allowed);

        // Thirty-one minutes later it is tomorrow, and the allowance is back - and a *new* instance
        // reads the same answer, because the counter is a row and not a dictionary.
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(31);

        var tomorrow = new EfExplainThisQuota(fixture.Scopes, fixture.Clock);
        var afterMidnight = await tomorrow.TryConsumeAsync(fixture.UserId, 3, TestContext.Current.CancellationToken);

        Assert.True(afterMidnight.Allowed);
        Assert.Equal(1, afterMidnight.Used);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), afterMidnight.ResetsAt);

        // Yesterday's count is still its own row until retention sweeps it.
        var days = await fixture.WithContextAsync(db => db.ExplainThisUsage
            .Where(usage => usage.UserId == fixture.UserId)
            .CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, days);
    }

    [Fact]
    public async Task ACapOfZeroRefusesWithoutRecordingASpendThatNeverHappened()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var quota = new EfExplainThisQuota(fixture.Scopes, fixture.Clock);

        var allowance = await quota.TryConsumeAsync(fixture.UserId, 0, TestContext.Current.CancellationToken);

        Assert.False(allowance.Allowed);
        Assert.Equal(0, allowance.Used);
        Assert.Equal(0, await quota.UsedTodayAsync(fixture.UserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SpentDaysAreSweptOnceTheyArePastRetention()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var quota = new EfExplainThisQuota(fixture.Scopes, fixture.Clock);
        await quota.TryConsumeAsync(fixture.UserId, 5, TestContext.Current.CancellationToken);

        // A counter row per user per day is small, but it is still a row per user per day forever.
        fixture.Clock.Now = fixture.Clock.Now.Add(EfExplainThisQuota.Retention).AddDays(1);
        await quota.TryConsumeAsync(fixture.UserId, 5, TestContext.Current.CancellationToken);

        var days = await fixture.WithContextAsync(db => db.ExplainThisUsage
            .Where(usage => usage.UserId == fixture.UserId)
            .CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, days);
    }
}
