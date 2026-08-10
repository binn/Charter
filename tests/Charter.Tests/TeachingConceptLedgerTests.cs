using Charter.Domain;
using Charter.Teaching;

namespace Charter.Tests;

/// <summary>
/// Section 13's concept ledger: every concept already explained to one person, passed into the
/// prompt as <em>already knows: X, Y, Z</em>, capped at a few dozen, and resettable. This is what
/// lets an <c>explain_everything</c> reader graduate over fifteen sessions without touching a
/// setting.
/// </summary>
public class TeachingConceptLedgerTests
{
    [Fact]
    public async Task AConceptAlreadyExplainedIsReferencedRatherThanReTaught()
    {
        var clock = new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        var concepts = new InMemoryConceptLedgerStore(clock);
        await concepts.RecordAsync(
            TeachingStubs.UserId,
            ["database migration", "preview environment"],
            TestContext.Current.CancellationToken);

        var (generator, client, _) = TeachingGeneratorTests.Build(new TeachingOptions(), concepts);

        await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var system = client.Requests[0].SystemPrompt!;

        Assert.Contains("Already knows:", system, StringComparison.Ordinal);
        Assert.Contains("database migration", system, StringComparison.Ordinal);
        Assert.Contains("preview environment", system, StringComparison.Ordinal);
        Assert.Contains("Do not re-teach or re-define any of them", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReaderWithAnEmptyLedgerIsToldNothingCanBeAssumed()
    {
        var (generator, client, _) = TeachingGeneratorTests.Build();

        await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var system = client.Requests[0].SystemPrompt!;

        Assert.Contains("first thing Charter has explained to this person", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Already knows:", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInjectionIsCappedAtAFewDozenMostRecentConcepts()
    {
        var clock = new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        var concepts = new InMemoryConceptLedgerStore(clock);

        for (var index = 0; index < 60; index++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            await concepts.RecordAsync(
                TeachingStubs.UserId,
                [$"concept-{index:00}"],
                TestContext.Current.CancellationToken);
        }

        var options = new TeachingOptions { ConceptInjectionLimit = 40 };

        var snapshot = ConceptLedgerSnapshot.From(
            await concepts.GetAsync(TeachingStubs.UserId, TestContext.Current.CancellationToken),
            options.ConceptInjectionLimit);

        Assert.Equal(40, snapshot.Concepts.Count);
        Assert.Equal(60, snapshot.TotalKnown);
        Assert.Equal("concept-59", snapshot.Concepts[0]);

        var (generator, client, _) = TeachingGeneratorTests.Build(options, concepts);

        await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var system = client.Requests[0].SystemPrompt!;

        // The 40 most recent are in; the 20 oldest are not, and the prompt says so rather than
        // pretending the list is complete.
        Assert.Contains("concept-59", system, StringComparison.Ordinal);
        Assert.Contains("concept-20", system, StringComparison.Ordinal);
        Assert.DoesNotContain("concept-19", system, StringComparison.Ordinal);
        Assert.DoesNotContain("concept-00", system, StringComparison.Ordinal);
        Assert.Contains("40 most recent of 60", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatOnePassExplainsIsWhatTheNextPassIsToldTheyKnow()
    {
        var clock = new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        var concepts = new InMemoryConceptLedgerStore(clock);

        var (generator, client, _) = TeachingGeneratorTests.Build(
            new TeachingOptions(),
            concepts,
            TeachingStubs.Walkthrough,
            TeachingStubs.Walkthrough);

        var first = await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "database table", "column" }, first.ConceptsExplained);
        Assert.DoesNotContain("Already knows:", client.Requests[0].SystemPrompt!, StringComparison.Ordinal);

        // A second session, same reader. The ledger is the reader's, not the session's.
        await generator.GetWalkthroughAsync(
            TeachingStubs.Request() with
            {
                Evidence = TeachingStubs.Evidence() with { SessionId = Guid.CreateVersion7() },
            },
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var system = client.Requests[1].SystemPrompt!;
        Assert.Contains("Already knows:", system, StringComparison.Ordinal);
        Assert.Contains("database table", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLedgerCanBeResetBecausePeopleForget()
    {
        var clock = new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        var concepts = new InMemoryConceptLedgerStore(clock);
        await concepts.RecordAsync(
            TeachingStubs.UserId,
            ["database migration"],
            TestContext.Current.CancellationToken);

        var (generator, _, _) = TeachingGeneratorTests.Build(new TeachingOptions(), concepts);

        await generator.ResetConceptLedgerAsync(TeachingStubs.UserId, TestContext.Current.CancellationToken);

        Assert.Empty(await concepts.GetAsync(TeachingStubs.UserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordingAKnownConceptReferencesItRatherThanDuplicatingIt()
    {
        var clock = new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        var concepts = new InMemoryConceptLedgerStore(clock);

        await concepts.RecordAsync(
            TeachingStubs.UserId,
            ["Database Migration"],
            TestContext.Current.CancellationToken);

        clock.Now = clock.Now.AddDays(3);
        await concepts.RecordAsync(
            TeachingStubs.UserId,
            ["database migration"],
            TestContext.Current.CancellationToken);

        var entries = await concepts.GetAsync(TeachingStubs.UserId, TestContext.Current.CancellationToken);
        var entry = Assert.Single(entries);

        Assert.Equal("database migration", entry.Concept);
        Assert.Equal(2, entry.TimesReferenced);
        Assert.True(entry.LastReferencedAt > entry.FirstExplainedAt);
    }

    [Fact]
    public void TheSnapshotAnswersWhetherAConceptIsAlreadyKnown()
    {
        var snapshot = ConceptLedgerSnapshot.From(
            [
                ConceptLedger.Record(
                    TeachingStubs.UserId,
                    "database migration",
                    new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)),
            ],
            40);

        Assert.True(snapshot.Knows("Database Migration"));
        Assert.False(snapshot.Knows("branch protection"));
        Assert.Empty(ConceptLedgerSnapshot.Empty.Concepts);
    }
}
