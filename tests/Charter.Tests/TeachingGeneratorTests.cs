using System.Text.Json;
using Charter.Domain;
using Charter.Models;
using Charter.Teaching;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 13: three surfaces at ascending cost, grounded in the session's real events, generated
/// lazily, calibrated for what the reader wants. Everything here runs against a stubbed
/// <see cref="IModelClient"/>.
/// </summary>
public class TeachingGeneratorTests
{
    [Fact]
    public async Task NothingIsGeneratedUntilTheReaderAsksForIt()
    {
        var (generator, client, _) = Build();
        var request = TeachingStubs.Request();

        // A session has finished. Its events, milestones and spec are all sitting right there. The
        // point of section 13's cost model is that none of that spends anything.
        Assert.Equal(0, client.Calls);

        var first = await generator.GetWalkthroughAsync(
            request,
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        Assert.False(first.ServedFromCache);
        Assert.True(first.Billable);

        // Opening the tab a second time costs nothing at all.
        var second = await generator.GetWalkthroughAsync(
            request,
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        Assert.True(second.ServedFromCache);
        Assert.False(second.Billable);
        Assert.Equal(first.BodyMarkdown, second.BodyMarkdown);
        Assert.Equal(0m, second.Charge.CostUsd);
    }

    [Fact]
    public async Task MoreDetailRegeneratesWithoutChangingTheStoredCalibration()
    {
        var (generator, client, _) = Build(TeachingStubs.Walkthrough, TeachingStubs.Walkthrough);
        var request = TeachingStubs.Request() with { Level = TeachingLevel.JustTheDecisions };

        await generator.GetWalkthroughAsync(
            request,
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var expanded = await generator.GetWalkthroughAsync(
            request with { Detail = TeachingDetail.MoreDetail, Regenerate = true },
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls);
        Assert.Equal(TeachingLevel.SkipTheBasics, expanded.Level);

        // The request's own stored calibration is untouched; only this rendering moved.
        Assert.Equal(TeachingLevel.JustTheDecisions, request.Level);
    }

    [Fact]
    public async Task TheThreeCalibrationsProduceMateriallyDifferentInstructionsAndBudgets()
    {
        var prompts = new List<string>();
        var budgets = new List<int>();

        foreach (var level in new[]
        {
            TeachingLevel.ExplainEverything,
            TeachingLevel.SkipTheBasics,
            TeachingLevel.JustTheDecisions,
        })
        {
            var (generator, client, _) = Build();

            await generator.GetWalkthroughAsync(
                TeachingStubs.Request() with { Level = level },
                TeachingStubs.Credential(),
                TestContext.Current.CancellationToken);

            prompts.Add(client.Requests[0].SystemPrompt!);
            budgets.Add(client.Requests[0].MaxOutputTokens);
        }

        Assert.Equal(3, prompts.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains("Assume no software vocabulary at all", prompts[0], StringComparison.Ordinal);
        Assert.Contains("in the same sentence, in plain words", prompts[0], StringComparison.Ordinal);

        Assert.Contains("Do not define", prompts[1], StringComparison.Ordinal);
        Assert.Contains("Spend the space on reasoning instead", prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Assume no software vocabulary", prompts[1], StringComparison.Ordinal);

        Assert.Contains("No mechanics", prompts[2], StringComparison.Ordinal);
        Assert.Contains("trade-offs and alternatives only", prompts[2], StringComparison.OrdinalIgnoreCase);

        // A shorter document, not the same document with a different preamble.
        Assert.True(budgets[0] > budgets[1]);
        Assert.True(budgets[1] > budgets[2]);
    }

    [Fact]
    public void TheCalibrationsAreNamedForWhatTheReaderWantsNotForWhatTheyLack()
    {
        string[] labels =
        [
            TeachingCalibration.Label(TeachingLevel.ExplainEverything),
            TeachingCalibration.Label(TeachingLevel.SkipTheBasics),
            TeachingCalibration.Label(TeachingLevel.JustTheDecisions),
        ];

        Assert.Equal(new[] { "explain_everything", "skip_the_basics", "just_the_decisions" }, labels);

        // Section 13: never label a human "beginner" in a UI their colleagues can see.
        string[] forbidden = ["beginner", "novice", "newbie", "expert", "advanced", "remedial", "level 1"];
        foreach (var label in labels.Concat(
            [
                TeachingCalibration.Describe(TeachingLevel.ExplainEverything),
                TeachingCalibration.Describe(TeachingLevel.SkipTheBasics),
                TeachingCalibration.Describe(TeachingLevel.JustTheDecisions),
            ]))
        {
            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, label, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task TheWalkthroughPromptCarriesTheSessionsRealEvents()
    {
        var (generator, client, _) = Build();

        await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var user = client.Requests[0].Messages[0].Content;

        Assert.Contains("src/Features/Quotes/QuoteLine.razor", user, StringComparison.Ordinal);
        Assert.Contains("dotnet test", user, StringComparison.Ordinal);
        Assert.Contains("Show the derate factor on a quote", user, StringComparison.Ordinal);
        Assert.Contains("SESSION-TRANSCRIPT", user, StringComparison.Ordinal);

        // ...and the system prompt insists on grounding rather than leaving it to chance.
        Assert.Contains(
            "Every sentence you write must come from those",
            client.Requests[0].SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySessionIsNotWorthAModelCall()
    {
        var (generator, client, _) = Build();

        var result = await generator.GetWalkthroughAsync(
            TeachingStubs.Request() with
            {
                Evidence = TeachingStubs.Evidence() with { Events = [], Milestones = [], ChangedFiles = [] },
            },
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, client.Calls);
        Assert.False(result.Billable);
        Assert.Contains("nothing to explain", result.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MilestonesAreAnnotatedInASingleCallWithOneSentenceEach()
    {
        var (generator, client, _) = Build(new
        {
            annotations = new[]
            {
                new { index = 0, sentence = "Charter read how quotes are stored today. It found the table." },
                new { index = 1, sentence = "It added a derate column to the quote lines table." },
                new { index = 2, sentence = "It ran the existing tests to check nothing else broke." },
            },
            concepts_explained = new[] { "database table", "automated test" },
        });

        var request = TeachingStubs.Request();
        var result = await generator.AnnotateMilestonesAsync(
            request,
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        // Three milestones, one call. Section 13: one call over the milestone list.
        Assert.Equal(1, client.Calls);
        Assert.Equal(3, result.Annotations.Count);

        // One sentence each: the extra sentence on the first annotation is dropped, not kept.
        Assert.Equal("Charter read how quotes are stored today.", result.Annotations[0].Sentence);
        Assert.All(result.Annotations, annotation => Assert.Single(
            annotation.Sentence.Split('.', StringSplitOptions.RemoveEmptyEntries)));

        var applied = result.ApplyTo(request.Evidence.Milestones);
        Assert.Equal(3, applied);
        Assert.Equal(
            "It added a derate column to the quote lines table.",
            request.Evidence.Milestones[1].AnnotationMd);
    }

    [Fact]
    public async Task ExplainThisIsCappedPerUserAndTheMessageNamesWhoCanRaiseIt()
    {
        var options = new TeachingOptions { ExplainThisPerUserPerDay = 2 };
        var (generator, client, _) = Build(options, TeachingStubs.Walkthrough, TeachingStubs.Walkthrough);

        var request = new ExplainThisRequest
        {
            UserId = TeachingStubs.UserId,
            Evidence = TeachingStubs.Evidence(),
            Target = new ExplainTarget(ExplainTargetKind.File, "src/Features/Quotes/QuoteLine.razor"),
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var allowed = await generator.ExplainAsync(
                request,
                TeachingStubs.Credential(),
                TestContext.Current.CancellationToken);

            Assert.True(allowed.Answered);
            Assert.NotNull(allowed.BodyMarkdown);
        }

        var refused = await generator.ExplainAsync(
            request,
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.False(refused.Answered);
        Assert.Equal(2, client.Calls);
        Assert.False(refused.Billable);
        Assert.NotNull(refused.CapMessage);

        // Section 34.5: a dead end that does not say who to ask is the fastest way to stop somebody
        // using the tool.
        Assert.Contains("administrator", refused.CapMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reset", refused.CapMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainThisAnswersTheThingThatWasClicked()
    {
        var (generator, client, _) = Build();

        await generator.ExplainAsync(
            new ExplainThisRequest
            {
                UserId = TeachingStubs.UserId,
                Evidence = TeachingStubs.Evidence(),
                Target = new ExplainTarget(
                    ExplainTargetKind.Hunk,
                    "src/Features/Quotes/QuoteLine.razor:41",
                    "+ <td>@line.DerateFactor.ToString(\"P0\")</td>"),
            },
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        var user = client.Requests[0].Messages[0].Content;
        Assert.Contains("What the reader clicked", user, StringComparison.Ordinal);
        Assert.Contains("QuoteLine.razor:41", user, StringComparison.Ordinal);
        Assert.Contains("DerateFactor", user, StringComparison.Ordinal);

        // Explain-this is the cheap surface, not a second walkthrough.
        Assert.True(client.Requests[0].MaxOutputTokens < new TeachingOptions().MaxOutputTokens);
    }

    [Fact]
    public async Task QuizzesProgressBarsAndStreaksAreStrippedOutOfWhateverComesBack()
    {
        var (generator, _, _) = Build(new
        {
            body_md =
                "Charter added a derate column to your quote lines.\n"
                + "Progress: 3 of 5 lessons complete.\n"
                + "You're on a streak — day 4 in a row!\n"
                + "Quiz: what is a database column?\n"
                + "The column stores a percentage against each line.",
            concepts_explained = new[] { "database column" },
        });

        var result = await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ToneStatementsRemoved);
        Assert.DoesNotContain("Progress:", result.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("streak", result.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quiz", result.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derate column", result.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("stores a percentage", result.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChargeIsReportedAgainstTheTeachingLedgerCategory()
    {
        var (generator, _, _) = Build();

        var result = await generator.GetWalkthroughAsync(
            TeachingStubs.Request(),
            TeachingStubs.Credential(),
            TestContext.Current.CancellationToken);

        // Section 34.6: teaching is its own budget line, so nothing downstream has to infer it.
        Assert.Equal(LedgerCategory.Teach, result.Category);
        Assert.Equal(0.042m, result.Charge.CostUsd);
        Assert.Equal(1200, result.Usage.InputTokens);

        var entity = result.ToEntity(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(result.SessionId, entity.SessionId);
        Assert.Equal(TeachingLevel.ExplainEverything, entity.Level);
        Assert.Equal(0.042m, entity.CostUsd);
    }

    internal static (TeachingGenerator Generator, RecapStubClient Client, IConceptLedgerStore Concepts) Build(
        params object[] payloads)
        => Build(new TeachingOptions(), payloads);

    internal static (TeachingGenerator Generator, RecapStubClient Client, IConceptLedgerStore Concepts) Build(
        TeachingOptions options,
        params object[] payloads)
        => Build(options, new InMemoryConceptLedgerStore(TimeProvider.System), payloads);

    internal static (TeachingGenerator Generator, RecapStubClient Client, IConceptLedgerStore Concepts) Build(
        TeachingOptions options,
        IConceptLedgerStore concepts,
        params object[] payloads)
    {
        var client = new RecapStubClient();
        foreach (var payload in payloads.Length > 0 ? payloads : [TeachingStubs.Walkthrough])
        {
            client.Enqueue(JsonSerializer.Serialize(payload));
        }

        var generator = new TeachingGenerator(
            new RecapStubClientFactory(client),
            new TeachingPromptBuilder(),
            concepts,
            new InMemoryWalkthroughStore(),
            new InMemoryExplainThisQuota(TimeProvider.System),
            options,
            NullLogger<TeachingGenerator>.Instance);

        return (generator, client, concepts);
    }
}

/// <summary>Shared fixtures for the teaching tests.</summary>
internal static class TeachingStubs
{
    public static readonly Guid UserId = Guid.Parse("0198b0f0-0000-7000-8000-0000000000aa");

    public static object Walkthrough { get; } = new
    {
        body_md =
            "Your quote wizard stores each line in a table called Quotes. Adding the derate "
            + "percentage meant one new column on that table, and one extra cell on the printed "
            + "quote.",
        concepts_explained = new[] { "database table", "column" },
    };

    public static ModelCredential Credential() => new()
    {
        Id = "grant-1",
        Kind = ModelCredentialKind.AnthropicApiKey,
        Secret = new ModelSecret("sk-test-not-a-real-key"),
    };

    public static IReadOnlyList<Milestone> Milestones() =>
    [
        Milestone.Promote(RecapStubs.SessionId, Guid.CreateVersion7(), MilestoneLabel.UnderstandingSetup),
        Milestone.Promote(RecapStubs.SessionId, Guid.CreateVersion7(), MilestoneLabel.MakingChanges),
        Milestone.Promote(RecapStubs.SessionId, Guid.CreateVersion7(), MilestoneLabel.CheckingItWorks),
    ];

    public static TeachingEvidence Evidence() => new()
    {
        SessionId = RecapStubs.SessionId,
        Spec = RecapStubs.Spec(),
        Milestones = Milestones(),
        Events = RecapStubs.Events(),
        ChangedFiles = ["src/Features/Quotes/QuoteLine.razor", "src/Auth/TokenIssuer.cs"],
        ProjectName = "Spectra",
    };

    public static TeachingRequest Request() => new()
    {
        UserId = UserId,
        Evidence = Evidence(),
    };
}
