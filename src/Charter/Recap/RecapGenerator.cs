using Charter.Domain;
using Charter.Models;
using Microsoft.Extensions.Logging;

namespace Charter.Recaps;

/// <summary>What one recap pass produced.</summary>
public sealed record RecapResult
{
    /// <summary>The session recapped.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The body, ready to post as a change request comment and to render in Charter.</summary>
    public required string BodyMarkdown { get; init; }

    /// <summary>The risk-ranked files, in the order an engineer should read them.</summary>
    public required IReadOnlyList<RecapRankedFile> RankedFiles { get; init; }

    /// <summary>The same ranking as <c>jsonb</c>, for <see cref="Domain.Recap.RiskItems"/>.</summary>
    public required string RiskItemsJson { get; init; }

    /// <summary>
    /// The structured recap, for <see cref="Domain.Recap.Payload"/> — the same content as
    /// <see cref="BodyMarkdown"/> before it was rendered, so the API reads it instead of parsing
    /// section headings back out of the prose.
    /// </summary>
    public required RecapDocument Document { get; init; }

    /// <summary>
    /// How many quality judgements <see cref="RecapVerdictGuard"/> removed from the model's answer.
    /// Non-zero is not an error — it is the guard doing the job the prompt could not guarantee.
    /// </summary>
    public int VerdictStatementsRemoved { get; init; }

    /// <summary>Token counts for the call (section 34.4 settles against these).</summary>
    public required ModelUsage Usage { get; init; }

    /// <summary>What the call cost, and against whose credential (section 20b.5).</summary>
    public required ModelCharge Charge { get; init; }

    /// <summary>
    /// The ledger line this spend belongs on (sections 5, 34.6). Charter's recap pass does not write
    /// the row itself — the orchestrator owns the reserve-then-settle transaction — but it names the
    /// category so nothing downstream has to guess.
    /// </summary>
    public LedgerCategory Category => LedgerCategory.Recap;

    /// <summary>
    /// Builds the persistable entity, payload and all. The caller saves it.
    /// </summary>
    /// <param name="bodyMarkdown">
    /// The body as it was actually published, when the publisher appended a fallback notice to it. A
    /// recap that reads differently in Charter from the way it reads on the change request is a
    /// recap two people quote at each other.
    /// </param>
    /// <param name="now">The clock.</param>
    public Domain.Recap ToEntity(string? bodyMarkdown = null, DateTimeOffset? now = null)
        => Domain.Recap.Generate(
            SessionId,
            string.IsNullOrWhiteSpace(bodyMarkdown) ? BodyMarkdown : bodyMarkdown,
            RiskItemsJson,
            Charge.CostUsd,
            now,
            payloadJson: Document.ToJson());
}

/// <summary>The engineer recap of section 14.</summary>
public interface IRecapGenerator
{
    /// <summary>Runs the recap pass over a finished session.</summary>
    /// <exception cref="RecapException">The model returned something unusable.</exception>
    Task<RecapResult> GenerateAsync(
        RecapEvidence evidence,
        ModelCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs one model pass over a finished session and turns it into section 14's five sections.
/// </summary>
/// <remarks>
/// <para>
/// The division of labour is the design. Charter decides the file ordering, the section structure,
/// the section 7.5 lead, and what is allowed to be said; the model supplies prose about what
/// happened. Everything an engineer relies on structurally is therefore deterministic and testable
/// without a model, and the parts that need a model are the parts where being approximately right is
/// acceptable.
/// </para>
/// <para>
/// This does not post the recap anywhere — see <see cref="IRecapPublisher"/> — and it does not write
/// a ledger row. It reports <see cref="RecapResult.Usage"/> and <see cref="RecapResult.Charge"/> so
/// whoever owns the budget transaction can settle against them under
/// <see cref="LedgerCategory.Recap"/>.
/// </para>
/// </remarks>
public sealed class RecapGenerator : IRecapGenerator
{
    private readonly IModelClientFactory _clients;
    private readonly RecapPromptBuilder _prompts;
    private readonly RecapOptions _options;
    private readonly ILogger<RecapGenerator> _logger;

    /// <summary>Creates a generator.</summary>
    public RecapGenerator(
        IModelClientFactory clients,
        RecapPromptBuilder prompts,
        RecapOptions options,
        ILogger<RecapGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _prompts = prompts;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RecapResult> GenerateAsync(
        RecapEvidence evidence,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(credential);

        // The files the session touched, however the caller sourced them. Where it passed none,
        // fall back to reading the transcript, because a recap with no file list has no section 3.
        var files = evidence.Files.Count > 0
            ? evidence.Files
            : RecapEventReader.ReadFileChanges(evidence.Events);

        var ranked = RecapFileRiskRanker.Rank(files, evidence.DenyPatterns);

        var request = new ModelRequest
        {
            Model = _options.Model,
            SystemPrompt = _prompts.BuildSystemPrompt(evidence),
            Messages = [ModelMessage.User(_prompts.BuildUserPrompt(evidence, ranked, _options))],
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature = _options.Temperature,
            ResponseFormat = RecapSchema.ResponseFormat,
            CorrelationId = evidence.SessionId.ToString(),
        };

        var client = _clients.GetClient(_options.Model);
        var completion = await client
            .CompleteAsync(request, credential, cancellationToken)
            .ConfigureAwait(false);

        var payload = RecapSchema.Parse(completion.StructuredJson ?? completion.Text);
        var (body, riskItems, document, verdictsRemoved) =
            RecapComposer.Compose(evidence, ranked, payload, _options);

        if (verdictsRemoved > 0)
        {
            // Section 14: the recap must never say the code looks good. The prompt says so and the
            // model said it anyway, which is exactly why the guard exists rather than the prompt
            // alone. Worth a line in the log: a model that does this constantly is worth replacing.
            _logger.LogWarning(
                "The recap model returned {Count} quality judgement(s) for session {SessionId}; "
                + "they were removed before the recap was published (section 14).",
                verdictsRemoved,
                evidence.SessionId);
        }

        if (evidence.AutoDispatched)
        {
            _logger.LogInformation(
                "Recap for auto-dispatched session {SessionId} leads with the unreviewed "
                + "specification and carries it in full (section 7.5).",
                evidence.SessionId);
        }

        return new RecapResult
        {
            SessionId = evidence.SessionId,
            BodyMarkdown = body,
            RankedFiles = ranked,
            RiskItemsJson = riskItems,
            Document = document,
            VerdictStatementsRemoved = verdictsRemoved,
            Usage = completion.Usage,
            Charge = completion.Charge,
        };
    }
}
