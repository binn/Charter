using System.Runtime.CompilerServices;
using System.Text.Json;
using Charter.Models;
using Charter.Refinement;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 10: raw request → clarifying conversation → refusal to dispatch anything still ambiguous
/// → spec confirmation card. Every test here runs against a stubbed <see cref="IModelClient"/>; no
/// test in this file makes a network call or spends a token.
/// </summary>
public class RefinementRefinerTests
{
    [Fact]
    public async Task AnAmbiguousRequestYieldsQuestionsRatherThanASpec()
    {
        var client = RefinementStubs.Returning(new
        {
            resolution = "clarify",
            message = "Before I write this down, a couple of questions.",
            clarifying_questions = new[]
            {
                "Which screen are you looking at when the total looks wrong?",
                "Should the derate apply to the whole quote or just that line?",
            },
        });

        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("the totals look off on some quotes"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var clarification = Assert.IsType<RefinementOutcome.NeedsClarification>(result.Outcome);
        Assert.Equal(2, clarification.Questions.Count);
        Assert.Null(result.Spec);
        Assert.Null(conversation.Spec);
        Assert.Contains(
            conversation.Turns,
            turn => turn.Kind == ConversationTurnKind.ClarifyingQuestion);
    }

    [Fact]
    public async Task ASpecThatStillCarriesOpenQuestionsIsSentBackForAnotherRound()
    {
        // The model claimed a spec. Charter does not take its word for it.
        var client = RefinementStubs.Returning(new
        {
            resolution = "spec",
            spec = new
            {
                title = "Fix the quote totals",
                outcome = "Quote totals will add up correctly.",
                acceptance_criteria = new[] { "The total matches the sum of the lines." },
                technical_approach = "Recalculate in QuoteTotals.",
                scope = new { files = new[] { "src/Features/Quotes/QuoteTotals.cs" }, paths = Array.Empty<string>() },
                open_questions = new[] { "Does the derate apply before or after tax?" },
            },
        });

        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("quote totals are wrong"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var clarification = Assert.IsType<RefinementOutcome.NeedsClarification>(result.Outcome);
        Assert.Contains("derate", clarification.Questions[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(conversation.Spec);
    }

    [Fact]
    public async Task AcceptanceCriteriaThatCannotBeCheckedAreTreatedAsAmbiguity()
    {
        var client = RefinementStubs.Returning(new
        {
            resolution = "spec",
            spec = new
            {
                title = "Tidy the quote screen",
                outcome = "The quote screen will be tidier.",
                acceptance_criteria = new[] { "The layout looks better, spacing improved etc." },
                scope = new { paths = new[] { "src/Features/Quotes/**" }, files = Array.Empty<string>() },
            },
        });

        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("make the quote screen nicer"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        Assert.IsType<RefinementOutcome.NeedsClarification>(result.Outcome);
        Assert.Null(conversation.Spec);
    }

    [Fact]
    public async Task ASettledRequestProducesASpecAndAConfirmationCard()
    {
        var client = RefinementStubs.Returning(RefinementStubs.GoodSpec);
        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("show the derate percentage on each quote line"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var proposed = Assert.IsType<RefinementOutcome.SpecProposed>(result.Outcome);
        Assert.False(proposed.RequiresEngineerReview);
        Assert.NotNull(conversation.Spec);

        var card = conversation.ConfirmationCard();
        Assert.True(card.CanConfirm);
        Assert.Contains("derate", card.Render().Markdown, StringComparison.OrdinalIgnoreCase);

        var approved = card.Confirm(Guid.CreateVersion7());
        Assert.Same(proposed.Spec.AcceptanceCriteria, approved.Spec.AcceptanceCriteria);
    }

    [Fact]
    public async Task ASpecTouchingDeniedPathsIsRefusedInPlainEnglishAndRoutedToAnEngineer()
    {
        var client = RefinementStubs.Returning(new
        {
            resolution = "spec",
            spec = new
            {
                title = "Let installers sign in with a phone number",
                outcome = "Installers will be able to sign in with a phone number instead of an email.",
                acceptance_criteria = new[] { "An installer can sign in using their phone number." },
                technical_approach = "Extend the identity provider configuration.",
                scope = new
                {
                    files = new[] { "src/Auth/SignInHandler.cs" },
                    paths = new[] { "src/Auth/**" },
                },
            },
        });

        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("let installers sign in with a phone number"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefinementOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.DeniedPaths, refused.Reason);

        // Plain English, and no repo path anywhere in it (section 7.4).
        Assert.Contains("sign-in and accounts", refused.RequesterMessage, StringComparison.Ordinal);
        Assert.Contains("engineer", refused.RequesterMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src/", refused.RequesterMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SignInHandler", refused.RequesterMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", refused.RequesterMessage, StringComparison.Ordinal);

        // The engineer gets the specifics.
        Assert.Contains("src/Auth/SignInHandler.cs", refused.EngineerDetail, StringComparison.Ordinal);

        Assert.Null(conversation.Spec);
    }

    [Fact]
    public async Task ARepoWithNoAllowListRefusesBeforeSpendingAToken()
    {
        var client = RefinementStubs.Returning(RefinementStubs.GoodSpec);
        var conversation = RefinementConversation.Start(InteractionMode.Plan);
        var context = RefinementContext.Bare;
        var refiner = RefinementStubs.Refiner(client);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("add a button"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefinementOutcome.Refused>(result.Outcome);
        Assert.Equal(RefusalReason.RepoNotConfigured, refused.Reason);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task GlossaryTermsAndStandardsReachThePrompt()
    {
        var client = RefinementStubs.Returning(RefinementStubs.GoodSpec);
        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("add the derate to the BOQ"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(client.Requests);
        var system = request.SystemPrompt;

        Assert.NotNull(system);
        Assert.Contains("BOQ", system, StringComparison.Ordinal);
        Assert.Contains("Bill of Quantities", system, StringComparison.Ordinal);
        Assert.Contains("derate", system, StringComparison.Ordinal);
        Assert.Contains("Reducing a rated output", system, StringComparison.Ordinal);

        // Section 8: the primer grounds the refiner in the codebase's shape.
        Assert.Contains("Blazor", system, StringComparison.Ordinal);

        // Section 26.3: standards are injected so refinement never proposes an off-policy service.
        Assert.Contains("openrouter", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("postgres", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never propose a library", system, StringComparison.Ordinal);

        // Section 8: the deny list is stated so the refiner does not propose it in the first place.
        Assert.Contains("src/Auth/**", system, StringComparison.Ordinal);

        // Structured output, not prose parsed with regexes.
        Assert.NotNull(request.ResponseFormat);
        Assert.Equal(SpecSchema.Name, request.ResponseFormat!.Name);
    }

    [Fact]
    public async Task ChatModeAnswersAndProducesNothing()
    {
        var client = RefinementStubs.Returning(new
        {
            resolution = "answer",
            message = "It already does that — the wizard picks the vertical from the site type.",
        });

        var (refiner, conversation, context) = Build(client, InteractionMode.Chat);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("how does the quote wizard decide which vertical to show?"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        var answered = Assert.IsType<RefinementOutcome.Answered>(result.Outcome);
        Assert.Contains("already does that", answered.RequesterMessage, StringComparison.Ordinal);
        Assert.Null(conversation.Spec);
        Assert.False(conversation.AllowsRepoWrite);
    }

    [Fact]
    public async Task ChatModeNeverProducesASpecEvenWhenTheModelReturnsOne()
    {
        var client = RefinementStubs.Returning(RefinementStubs.GoodSpec);
        var (refiner, conversation, context) = Build(client, InteractionMode.Chat);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("show the derate percentage"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        Assert.IsType<RefinementOutcome.Answered>(result.Outcome);
        Assert.Null(conversation.Spec);
    }

    [Fact]
    public async Task RefusingCostsNothingAndTheUsageIsReportedForTheLedger()
    {
        var client = RefinementStubs.Returning(RefinementStubs.GoodSpec);
        var (refiner, conversation, context) = Build(client, InteractionMode.Plan);

        var result = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("show the derate percentage on each quote line"),
            context,
            Credential(),
            TestContext.Current.CancellationToken);

        Assert.Equal(120, result.Usage.InputTokens);
        Assert.Equal(InteractionMode.Plan, result.Mode);
    }

    private static (ISpecRefiner Refiner, RefinementConversation Conversation, RefinementContext Context)
        Build(RefinementStubClient client, InteractionMode mode) =>
        (RefinementStubs.Refiner(client),
            RefinementConversation.Start(mode),
            RefinementStubs.Context());

    private static ModelCredential Credential() => new()
    {
        Id = "grant-1",
        Kind = ModelCredentialKind.AnthropicApiKey,
        Secret = new ModelSecret("sk-test-not-a-real-key"),
    };
}

/// <summary>
/// A stubbed <see cref="IModelClient"/> that answers from a canned JSON payload and records the
/// requests it was given. Nothing in the refinement tests touches the network.
/// </summary>
internal sealed class RefinementStubClient : IModelClient
{
    private readonly Queue<string> _responses = new();

    public List<ModelRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public ModelProvider Provider => ModelProvider.Anthropic;

    public IReadOnlyCollection<ModelProvider> SupportedProviders => [ModelProvider.Anthropic];

    public bool Supports(ModelIdentifier model) => true;

    public RefinementStubClient Enqueue(string json)
    {
        _responses.Enqueue(json);
        return this;
    }

    public Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The refinement stub ran out of canned responses.");
        }

        var json = _responses.Dequeue();
        return Task.FromResult(new ModelCompletion
        {
            Model = request.Model,
            Text = json,
            StructuredJson = json,
            StopReason = ModelStopReason.EndTurn,
            Usage = new ModelUsage { InputTokens = 120, OutputTokens = 40 },
            Charge = ModelCharge.None,
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
internal sealed class RefinementStubClientFactory : IModelClientFactory
{
    private readonly IModelClient _client;

    public RefinementStubClientFactory(IModelClient client) => _client = client;

    public IModelClient GetClient(ModelIdentifier model) => _client;

    public IModelClient GetClient(ModelProvider provider) => _client;

    public IModelClient? Find(ModelProvider provider) => _client;
}

/// <summary>Shared fixtures for the refinement tests.</summary>
internal static class RefinementStubs
{
    public const string Glossary = """
        version: 1
        BOQ: "Bill of Quantities — the itemised list of equipment and materials in a quote."
        derate: "Reducing a rated output to account for real-world losses like heat or shading."
        """;

    public const string Standards = """
        version: 3
        stacks:
          web:
            backend:   { runtime: "dotnet", version: "10", required: true }
            database:  { engine: "postgres", min_version: "16" }
        services:
          ai:      { provider: "openrouter", required: true }
          hosting: { provider: "railway" }
        required_files:
          - ".charter/config.yml"
        conventions:
          branch: "main"
        """;

    public const string Primer = "This is a Blazor server app. Quotes live in src/Features/Quotes.";

    public static object GoodSpec { get; } = new
    {
        resolution = "spec",
        message = "Here's what I've understood.",
        spec = new
        {
            title = "Show the derate factor on a quote",
            outcome = "Every quote line will show the derate percentage next to the rated output.",
            acceptance_criteria = new[]
            {
                "Open any quote and each line shows a derate percentage.",
                "Printing a quote includes the derate percentage.",
            },
            technical_approach = "Render the existing DerateFactor on QuoteLine.razor.",
            scope = new
            {
                files = new[] { "src/Features/Quotes/QuoteLine.razor" },
                paths = new[] { "src/Features/Quotes/**" },
            },
            risks = new[] { "Printed quotes have a fixed column width." },
            open_questions = Array.Empty<string>(),
        },
    };

    public static RefinementScopePolicy Scope { get; } = new(
        ["src/Features/**", "src/Web/Components/**"],
        ["src/Auth/**", "**/Migrations/**", ".github/**", "infra/**", "**/appsettings*.json"]);

    public static RefinementStubClient Returning(object payload) =>
        new RefinementStubClient().Enqueue(JsonSerializer.Serialize(payload));

    public static RefinementContext Context() => new()
    {
        Glossary = GlossaryDocument.Parse(Glossary),
        Standards = StandardsDocument.Parse(Standards),
        PrimerMarkdown = Primer,
        Scope = Scope,
        ProjectName = "Quotes",
    };

    public static SpecRefiner Refiner(IModelClient client) => new(
        new RefinementStubClientFactory(client),
        new RefinementPromptBuilder(),
        new RefinementOptions(),
        CharterTime.System,
        NullLogger<SpecRefiner>.Instance);

    public static SpecDocument Spec(
        string? title = null,
        SpecScope? scope = null,
        IEnumerable<string>? openQuestions = null) => SpecDocument.Create(
        title ?? "Show the derate factor on a quote",
        "Every quote line will show the derate percentage next to the rated output.",
        ["Open any quote and each line shows a derate percentage."],
        "Render the existing DerateFactor on QuoteLine.razor.",
        scope ?? SpecScope.Of(["src/Features/Quotes/QuoteLine.razor"], null),
        ["Printed quotes have a fixed column width."],
        openQuestions);
}
