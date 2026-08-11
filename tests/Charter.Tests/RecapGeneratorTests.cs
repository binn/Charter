using System.Runtime.CompilerServices;
using System.Text.Json;
using Charter.Domain;
using Charter.Models;
using Charter.Recaps;
using Charter.Refinement;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 14: five sections, in order, over the session's real events — and never a verdict. Every
/// test here runs against a stubbed <see cref="IModelClient"/>; nothing makes a network call or
/// spends a token.
/// </summary>
public class RecapGeneratorTests
{
    [Fact]
    public async Task TheFiveSectionsAppearInSpecificationOrder()
    {
        var (generator, _) = Build(RecapStubs.GoodAnswer);

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        var body = result.BodyMarkdown;

        var order = new[]
        {
            "### 1. What changed, and why",
            "### 2. Where this deviated from the specification",
            "### 3. Files, ranked by risk",
            "### 4. What could not be verified",
            "### 5. Suggested review order",
        };

        var positions = order.Select(heading => body.IndexOf(heading, StringComparison.Ordinal)).ToList();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToList(), positions);
    }

    [Fact]
    public async Task ItNeverSaysTheCodeLooksGoodEvenWhenTheModelInsists()
    {
        // The stub does what a real model does when it is pleased with a diff: it congratulates.
        // Section 14 forbids that outright, so the prompt says so and the guard removes it anyway.
        var (generator, client) = Build(new
        {
            what_and_why = "The agent added a derate column to the quote lines. The implementation "
                + "looks good and is well written. No issues were found.",
            deviations = new[]
            {
                new
                {
                    what = "Reused the existing DerateFactor property instead of adding a new one. "
                        + "This is a clean implementation and I would approve it.",
                    spec_said = "Add a derate percentage to each line",
                    why = "The property already existed",
                    where = "src/Features/Quotes/QuoteLine.razor",
                },
            },
            file_notes = new[]
            {
                new
                {
                    path = "src/Auth/TokenIssuer.cs",
                    note = "Adds a claim. Looks correct and is ready to merge.",
                },
            },
            could_not_verify = new[]
            {
                "No tests were written for the printed quote path.",
                "Everything else is fine and safe to ship.",
            },
        });

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        var body = result.BodyMarkdown;

        string[] forbidden =
        [
            "looks good", "well written", "no issues", "clean implementation", "would approve",
            "looks correct", "ready to merge", "safe to ship", "everything else is fine",
        ];

        foreach (var phrase in forbidden)
        {
            Assert.DoesNotContain(phrase, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(result.VerdictStatementsRemoved >= 4);

        // What survived is the factual half of each sentence, not a blanked-out document.
        Assert.Contains("derate column", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No tests were written", body, StringComparison.Ordinal);
        Assert.Contains("Adds a claim", body, StringComparison.Ordinal);

        // The rule is in the prompt too, not only in the filter.
        Assert.Contains(
            "NEVER assess quality",
            client.Requests[0].SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            RecapVerdictGuard.Disclaimer,
            body,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("This all looks good to me.")]
    [InlineData("LGTM.")]
    [InlineData("The change is correct.")]
    [InlineData("No concerns here.")]
    [InlineData("Nicely structured refactor.")]
    [InlineData("I would approve this.")]
    [InlineData("Ready to merge.")]
    [InlineData("Properly implemented throughout.")]
    [InlineData("No further review is needed.")]
    [InlineData("Good quality work.")]
    public void EveryShapeOfPraiseIsRecognisedAsAVerdict(string sentence)
        => Assert.True(RecapVerdictGuard.IsVerdict(sentence));

    [Theory]
    [InlineData("The change was made against the approved spec.")]
    [InlineData("No human approved this specification before the build.")]
    [InlineData("The migration drops a column, which section 15 classifies as destructive.")]
    [InlineData("Tests were not written for the printed quote path.")]
    public void StatementsOfFactAreNotMistakenForVerdicts(string sentence)
        => Assert.False(RecapVerdictGuard.IsVerdict(sentence));

    [Fact]
    public async Task AnAutoDispatchedSessionLeadsWithTheUnreviewedSpecAndCarriesItInFull()
    {
        var (generator, client) = Build(RecapStubs.GoodAnswer);

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(autoDispatched: true),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        var body = result.BodyMarkdown;

        // Section 7.5: it leads. Nothing about the change appears before the fact that nobody
        // approved the specification.
        var lead = body.IndexOf("No human approved this specification", StringComparison.Ordinal);
        var firstSection = body.IndexOf("### 1. What changed", StringComparison.Ordinal);
        Assert.True(lead >= 0);
        Assert.True(lead < firstSection);

        // ...and in full, not summarised: title, outcome, every acceptance criterion, the technical
        // approach and the recorded risks are all present verbatim.
        Assert.Contains("### The specification, in full", body, StringComparison.Ordinal);
        var spec = RecapStubs.Spec();
        Assert.Contains(spec.Title, body, StringComparison.Ordinal);
        Assert.Contains(spec.Outcome, body, StringComparison.Ordinal);
        Assert.Contains(spec.TechnicalApproach!, body, StringComparison.Ordinal);
        foreach (var criterion in spec.AcceptanceCriteria)
        {
            Assert.Contains(criterion, body, StringComparison.Ordinal);
        }

        Assert.Contains("auto-dispatched", client.Requests[0].SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnApprovedSessionDoesNotClaimNobodyApprovedIt()
    {
        var (generator, _) = Build(RecapStubs.GoodAnswer);

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("No human approved", result.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("The specification, in full", result.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRiskRankingDrivesTheBodyTheReviewOrderAndTheStoredRiskItems()
    {
        var (generator, client) = Build(RecapStubs.GoodAnswer);

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal("src/Auth/TokenIssuer.cs", result.RankedFiles[0].Path);

        using var risk = JsonDocument.Parse(result.RiskItemsJson);
        var first = risk.RootElement[0];
        Assert.Equal("src/Auth/TokenIssuer.cs", first.GetProperty("path").GetString());
        Assert.Equal(1, first.GetProperty("review_order").GetInt32());
        Assert.Equal("critical", first.GetProperty("band").GetString());

        // The review order starts where the risk is, and the model was handed the order rather than
        // asked for it.
        var reviewSection = result.BodyMarkdown[
            result.BodyMarkdown.IndexOf("### 5. Suggested review order", StringComparison.Ordinal)..];
        Assert.Contains("1. `src/Auth/TokenIssuer.cs`", reviewSection, StringComparison.Ordinal);
        Assert.Contains(
            "already ordered by risk",
            client.Requests[0].Messages[0].Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFileListIsReadFromTheTranscriptWhenTheCallerSuppliesNone()
    {
        var (generator, _) = Build(RecapStubs.GoodAnswer);

        var evidence = RecapStubs.Evidence() with { Files = [] };
        var result = await generator.GenerateAsync(
            evidence,
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.RankedFiles, file => file.Path == "src/Features/Quotes/QuoteLine.razor");
        Assert.Contains(result.RankedFiles, file => file.Path == "src/Auth/TokenIssuer.cs");
    }

    [Fact]
    public async Task TheChargeIsReportedAgainstTheRecapLedgerCategory()
    {
        var (generator, _) = Build(RecapStubs.GoodAnswer);

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(LedgerCategory.Recap, result.Category);
        Assert.Equal(1200, result.Usage.InputTokens);
        Assert.Equal(300, result.Usage.OutputTokens);
        Assert.Equal(0.042m, result.Charge.CostUsd);

        var entity = result.ToEntity(now: new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(result.SessionId, entity.SessionId);
        Assert.Equal(0.042m, entity.CostUsd);
        Assert.Equal(result.RiskItemsJson, entity.RiskItems);

        // The structured recap travels with the row, so nothing downstream has to parse the prose
        // back into sections to serve it.
        Assert.Equal(result.Document.ToJson(), entity.Payload);
    }

    [Fact]
    public async Task AMissingDeviationsSectionStillRendersTheHeadingAndSaysWhatItMeans()
    {
        var (generator, _) = Build(new
        {
            what_and_why = "Added a derate column to quote lines.",
            deviations = Array.Empty<object>(),
            could_not_verify = Array.Empty<string>(),
        });

        var result = await generator.GenerateAsync(
            RecapStubs.Evidence(),
            RecapStubs.Credential(),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "### 2. Where this deviated from the specification",
            result.BodyMarkdown,
            StringComparison.Ordinal);
        Assert.Contains(
            "a statement about the transcript, not about the change",
            result.BodyMarkdown,
            StringComparison.Ordinal);
        Assert.Contains(
            "not as evidence that everything was checked",
            result.BodyMarkdown,
            StringComparison.Ordinal);
    }

    private static (RecapGenerator Generator, RecapStubClient Client) Build(object payload)
    {
        var client = new RecapStubClient().Enqueue(JsonSerializer.Serialize(payload));
        var generator = new RecapGenerator(
            new RecapStubClientFactory(client),
            new RecapPromptBuilder(),
            new RecapOptions(),
            NullLogger<RecapGenerator>.Instance);

        return (generator, client);
    }
}

/// <summary>A stubbed <see cref="IModelClient"/> that answers from canned JSON.</summary>
internal sealed class RecapStubClient : IModelClient
{
    private readonly Queue<string> _responses = new();

    public List<ModelRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public ModelProvider Provider => ModelProvider.Anthropic;

    public IReadOnlyCollection<ModelProvider> SupportedProviders => [ModelProvider.Anthropic];

    public bool Supports(ModelIdentifier model) => true;

    public RecapStubClient Enqueue(string json)
    {
        _responses.Enqueue(json);
        return this;
    }

    public RecapStubClient Enqueue(object payload) => Enqueue(JsonSerializer.Serialize(payload));

    public Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The stub ran out of canned responses.");
        }

        var json = _responses.Dequeue();

        return Task.FromResult(new ModelCompletion
        {
            Model = request.Model,
            Text = json,
            StructuredJson = json,
            StopReason = ModelStopReason.EndTurn,
            Usage = new ModelUsage { InputTokens = 1200, OutputTokens = 300 },
            Charge = new ModelCharge
            {
                Unit = ModelChargeUnit.Usd,
                CostUsd = 0.042m,
                NotionalCostUsd = 0.042m,
                Basis = ModelCostBasis.Estimated,
                CredentialId = credential.Id,
            },
        });
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        ModelCredential credential,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completion = await CompleteAsync(request, credential, cancellationToken);
        yield return new ModelStreamEvent.Completed(completion);
    }
}

/// <summary>An <see cref="IModelClientFactory"/> over a single stub.</summary>
internal sealed class RecapStubClientFactory : IModelClientFactory
{
    private readonly IModelClient _client;

    public RecapStubClientFactory(IModelClient client) => _client = client;

    public IModelClient GetClient(ModelIdentifier model) => _client;

    public IModelClient GetClient(ModelProvider provider) => _client;

    public IModelClient? Find(ModelProvider provider) => _client;
}

/// <summary>Shared fixtures for the recap and teaching tests.</summary>
internal static class RecapStubs
{
    public static readonly Guid SessionId = Guid.Parse("0198b0f0-0000-7000-8000-00000000abcd");

    public static object GoodAnswer { get; } = new
    {
        what_and_why = "The session added a derate column to quote lines, as the approved spec asked.",
        deviations = new[]
        {
            new
            {
                what = "Reused the existing DerateFactor property rather than adding a column",
                spec_said = "Add a derate percentage to each line",
                why = "The property already existed on QuoteLine",
                where = "src/Features/Quotes/QuoteLine.razor",
            },
        },
        file_notes = new[]
        {
            new { path = "src/Auth/TokenIssuer.cs", note = "Adds a claim carrying the derate scope." },
            new { path = "src/Features/Quotes/QuoteLine.razor", note = "Renders the derate percentage." },
        },
        could_not_verify = new[] { "No tests were written for the printed quote path." },
    };

    public static ModelCredential Credential() => new()
    {
        Id = "grant-1",
        Kind = ModelCredentialKind.AnthropicApiKey,
        Secret = new ModelSecret("sk-test-not-a-real-key"),
    };

    public static SpecDocument Spec() => SpecDocument.Create(
        "Show the derate factor on a quote",
        "Every quote line will show the derate percentage next to the rated output.",
        [
            "Open any quote and each line shows a derate percentage.",
            "Printing a quote includes the derate percentage.",
        ],
        "Render the existing DerateFactor on QuoteLine.razor.",
        SpecScope.Of(["src/Features/Quotes/QuoteLine.razor"], ["src/Features/Quotes/**"]),
        ["Printed quotes have a fixed column width."]);

    public static IReadOnlyList<Event> Events() =>
    [
        Event.Append(SessionId, 1, EventTypes.SessionStarted, """{"agent":"claude-code"}"""),
        Event.Append(
            SessionId,
            2,
            EventTypes.FileWrite,
            """{"path":"src/Features/Quotes/QuoteLine.razor","lines_added":9}"""),
        Event.Append(
            SessionId,
            3,
            EventTypes.FileWrite,
            """{"tool_input":{"file_path":"src/Auth/TokenIssuer.cs"}}"""),
        Event.Append(
            SessionId,
            4,
            EventTypes.Command,
            """{"command":"dotnet test","exit_code":0}"""),
        Event.Append(
            SessionId,
            5,
            EventTypes.Message,
            """{"text":"DerateFactor already existed so I reused it."}"""),
    ];

    public static RecapEvidence Evidence(bool autoDispatched = false) => new()
    {
        SessionId = SessionId,
        Spec = Spec(),
        AutoDispatched = autoDispatched,
        ApprovedBy = autoDispatched ? null : "Priya",
        Files =
        [
            new RecapFileChange("src/Features/Quotes/QuoteLine.razor") { LinesAdded = 9 },
            new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 4 },
            new RecapFileChange("tests/Quotes/RenderingTests.cs") { LinesAdded = 30 },
        ],
        Events = Events(),
        DenyPatterns = ["src/Auth/**", "**/Migrations/**"],
    };
}
