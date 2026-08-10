using Charter.Models;
using Microsoft.Extensions.Logging;

namespace Charter.Refinement;

/// <summary>Knobs for refinement. Registered with defaults; the host may replace it.</summary>
public sealed record RefinementOptions
{
    /// <summary>The model refinement runs on.</summary>
    public ModelIdentifier Model { get; init; } = ModelIdentifier.Parse("claude-sonnet-4-6");

    /// <summary>Upper bound on generated tokens per turn.</summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>Sampling temperature. Refinement wants consistency, not variety.</summary>
    public double? Temperature { get; init; }
}

/// <summary>The refinement conversation of section 10.</summary>
public interface ISpecRefiner
{
    /// <summary>
    /// Advances a conversation by one turn against what the requester just said.
    /// </summary>
    Task<RefinementResult> AdvanceAsync(
        RefinementConversation conversation,
        RequesterText message,
        RefinementContext context,
        ModelCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a raw request into a confirmable spec, or refuses to (section 10).
/// </summary>
/// <remarks>
/// <para>
/// The refiner's most important behaviour is what it declines to do. It will not produce a spec
/// while anything material is unsettled, and that is enforced here rather than delegated to the
/// model: a response that claims to be a spec but carries open questions, or acceptance criteria
/// hedged with <em>TBD</em>, is turned back into clarifying questions by
/// <see cref="AmbiguityGuard"/>. No agent runs and no tokens burn on a vague request.
/// </para>
/// <para>
/// It is also the sanitisation boundary of section 16. Untrusted text goes in; a structured,
/// model-authored <see cref="SpecDocument"/> comes out; instruction-shaped language in the input is
/// flagged for an engineer on the way through. What the agent eventually receives is an
/// <see cref="AgentBriefing"/>, which can only be built from a confirmed spec.
/// </para>
/// </remarks>
public sealed class SpecRefiner : ISpecRefiner
{
    private readonly IModelClientFactory _clients;
    private readonly RefinementPromptBuilder _prompts;
    private readonly RefinementOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SpecRefiner> _logger;

    /// <summary>Creates a refiner.</summary>
    public SpecRefiner(
        IModelClientFactory clients,
        RefinementPromptBuilder prompts,
        RefinementOptions options,
        TimeProvider time,
        ILogger<SpecRefiner> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _prompts = prompts;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RefinementResult> AdvanceAsync(
        RefinementConversation conversation,
        RequesterText message,
        RefinementContext context,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);

        if (conversation.Mode == InteractionMode.Build)
        {
            throw new InvalidOperationException(
                "A build is not a refinement conversation. Promote back through Plan to revise a spec.");
        }

        var now = _time.GetUtcNow();
        var flags = conversation.RecordRequesterMessage(message, now);
        if (flags.Count > 0)
        {
            _logger.LogWarning(
                "Refinement flagged {Count} instruction-shaped signal(s) on conversation {Conversation}; "
                + "the request will not dispatch until an engineer reviews it.",
                flags.Count,
                conversation.Id);
        }

        // Section 7.3: deny by default. A repo with no allow list is requestable by nobody, and
        // there is no point spending tokens discovering that.
        if (conversation.Mode == InteractionMode.Plan && context.Scope.Allow.Count == 0)
        {
            return Record(
                conversation,
                new RefinementOutcome.Refused(
                    RefusalReason.RepoNotConfigured,
                    "This project isn't set up for requests yet. I've let an engineer know.",
                    "The repo has no scopes.allow entries in .charter/config.yml, so nothing is "
                    + "requestable (section 7.3, deny by default)."),
                ModelUsage.Empty,
                ModelCharge.None,
                now);
        }

        var request = new ModelRequest
        {
            Model = _options.Model,
            SystemPrompt = _prompts.BuildSystemPrompt(context, conversation.Mode),
            Messages = _prompts.BuildMessages(conversation),
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature = _options.Temperature,
            ResponseFormat = SpecSchema.ResponseFormat,
            CorrelationId = conversation.Id.ToString(),
        };

        var client = _clients.GetClient(_options.Model);
        var completion = await client
            .CompleteAsync(request, credential, cancellationToken)
            .ConfigureAwait(false);

        var payload = SpecSchema.Parse(completion.StructuredJson ?? completion.Text);
        var outcome = Interpret(payload, conversation, context, flags);

        return Record(conversation, outcome, completion.Usage, completion.Charge, now);
    }

    private RefinementOutcome Interpret(
        RefinementPayload payload,
        RefinementConversation conversation,
        RefinementContext context,
        IReadOnlyList<InjectionSignal> flags)
    {
        if (conversation.Mode == InteractionMode.Chat)
        {
            // Chat produces nothing, whatever the model returned. Section 10b: a request that never
            // becomes a session is the cheapest possible outcome, and chat is where that happens.
            return new RefinementOutcome.Answered(
                payload.Message
                ?? "I couldn't find an answer to that in what I know about this project.");
        }

        switch (payload.ParsedResolution)
        {
            case RefinementResolution.Refuse:
                return new RefinementOutcome.Refused(
                    RefusalReason.ModelDeclined,
                    payload.Message
                    ?? "I can't take this one on. I've passed it to an engineer.",
                    payload.Message ?? "The refiner declined without giving a reason.");

            case RefinementResolution.Answer:
            case RefinementResolution.Clarify:
                return Clarify(payload.ClarifyingQuestions, payload.Message);

            case RefinementResolution.Spec:
                break;

            default:
                return Clarify(payload.ClarifyingQuestions, payload.Message);
        }

        SpecDocument? spec = null;
        string? problem = "the refiner returned no spec";
        if (payload.Spec is not null)
        {
            payload.Spec.TryToDocument(out spec, out problem);
        }

        if (spec is null)
        {
            _logger.LogWarning(
                "The refiner claimed a spec on conversation {Conversation} but {Problem}; asking again.",
                conversation.Id,
                problem);

            return Clarify(payload.ClarifyingQuestions, payload.Message);
        }

        // Section 10: refuse to dispatch anything still ambiguous. This check is Charter's, not the
        // model's — a model asked whether it is confused will sometimes say no.
        var unresolved = AmbiguityGuard.UnresolvedQuestions(spec);
        if (unresolved.Count > 0)
        {
            _logger.LogInformation(
                "Refinement withheld a spec on conversation {Conversation}: {Count} point(s) still open.",
                conversation.Id,
                unresolved.Count);

            return new RefinementOutcome.NeedsClarification(
                unresolved,
                "Almost there — a couple of things I need to pin down before we go ahead.");
        }

        // Section 10: refuse to produce a spec touching denied paths, explain in plain English, and
        // route to an engineer.
        var scope = context.Scope.Evaluate(spec.Scope);
        if (!scope.IsAllowed)
        {
            _logger.LogInformation(
                "Refinement refused a spec on conversation {Conversation}: scope violations.",
                conversation.Id);

            return new RefinementOutcome.Refused(
                RefusalReason.DeniedPaths,
                scope.RequesterMessage,
                scope.EngineerDetail);
        }

        return new RefinementOutcome.SpecProposed(spec, flags);
    }

    private static RefinementOutcome Clarify(IReadOnlyList<string>? questions, string? message)
    {
        var cleaned = SpecText.CleanList(questions);
        if (cleaned.Count == 0)
        {
            cleaned =
            [
                message is { Length: > 0 } fallback
                    ? fallback
                    : "Can you tell me a bit more about what you'd like to be different?",
            ];
        }

        return new RefinementOutcome.NeedsClarification(
            cleaned,
            message ?? "Before I write this down, a couple of questions.");
    }

    private static RefinementResult Record(
        RefinementConversation conversation,
        RefinementOutcome outcome,
        ModelUsage usage,
        ModelCharge charge,
        DateTimeOffset now)
    {
        switch (outcome)
        {
            case RefinementOutcome.NeedsClarification clarification:
                conversation.RecordCharterTurn(
                    ConversationTurnKind.ClarifyingQuestion,
                    string.Join("\n", clarification.Questions.Select(static q => $"- {q}")),
                    now);
                break;

            case RefinementOutcome.Refused refused:
                conversation.RecordCharterTurn(
                    ConversationTurnKind.Refusal,
                    refused.RequesterMessage,
                    now);
                break;

            case RefinementOutcome.Answered answered:
                conversation.RecordCharterTurn(
                    ConversationTurnKind.Answer,
                    answered.RequesterMessage,
                    now);
                break;

            case RefinementOutcome.SpecProposed proposed:
                conversation.ProposeSpec(proposed.Spec, now);
                break;
        }

        return new RefinementResult(outcome, conversation.Mode, usage, charge);
    }
}
