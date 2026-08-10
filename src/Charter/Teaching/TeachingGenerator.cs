using Charter.Domain;
using Charter.Models;
using Microsoft.Extensions.Logging;

namespace Charter.Teaching;

/// <summary>One request for teaching about a finished session.</summary>
public sealed record TeachingRequest
{
    /// <summary>Who is reading. The concept ledger is theirs, not the session's.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The session's real events.</summary>
    public required TeachingEvidence Evidence { get; init; }

    /// <summary>The reader's stored calibration.</summary>
    public TeachingLevel Level { get; init; } = TeachingLevel.ExplainEverything;

    /// <summary>The per-walkthrough override, which never changes the stored calibration.</summary>
    public TeachingDetail Detail { get; init; } = TeachingDetail.AsSet;

    /// <summary>
    /// Regenerate even if one is stored. What the <em>more detail</em> / <em>less detail</em>
    /// buttons set, alongside <see cref="Detail"/>.
    /// </summary>
    public bool Regenerate { get; init; }

    /// <summary>The calibration this request actually renders at.</summary>
    public TeachingLevel EffectiveLevel => TeachingCalibration.Apply(Level, Detail);
}

/// <summary>One <em>explain this</em> click.</summary>
public sealed record ExplainThisRequest
{
    /// <summary>Who clicked.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The session's real events.</summary>
    public required TeachingEvidence Evidence { get; init; }

    /// <summary>What they clicked.</summary>
    public required ExplainTarget Target { get; init; }

    /// <summary>The reader's stored calibration.</summary>
    public TeachingLevel Level { get; init; } = TeachingLevel.ExplainEverything;

    /// <summary>The per-answer override.</summary>
    public TeachingDetail Detail { get; init; } = TeachingDetail.AsSet;

    /// <summary>The calibration this answer renders at.</summary>
    public TeachingLevel EffectiveLevel => TeachingCalibration.Apply(Level, Detail);
}

/// <summary>What one teaching pass cost and produced.</summary>
public abstract record TeachingOutcome
{
    /// <summary>The session taught about.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The calibration it rendered at.</summary>
    public required TeachingLevel Level { get; init; }

    /// <summary>Token counts for the call, or empty when nothing was spent.</summary>
    public required ModelUsage Usage { get; init; }

    /// <summary>What the call cost, and against whose credential (section 20b.5).</summary>
    public required ModelCharge Charge { get; init; }

    /// <summary>Concepts newly recorded against the reader's ledger by this pass.</summary>
    public IReadOnlyList<string> ConceptsExplained { get; init; } = [];

    /// <summary>How many lines <see cref="TeachingToneGuard"/> removed.</summary>
    public int ToneStatementsRemoved { get; init; }

    /// <summary>
    /// The ledger line this spend belongs on. Section 34.6: teaching is <em>its own line</em>,
    /// because bundled with build spend it is the first thing an admin cuts — it is the item with no
    /// immediately visible output. Naming it separately is what protects it.
    /// </summary>
    public LedgerCategory Category => LedgerCategory.Teach;

    /// <summary>Whether this pass spent anything at all.</summary>
    public bool Billable => Usage.TotalInputTokens > 0 || Usage.OutputTokens > 0;
}

/// <summary>The walkthrough — section 13's main event.</summary>
public sealed record TeachingWalkthroughResult : TeachingOutcome
{
    /// <summary>The narrative.</summary>
    public required string BodyMarkdown { get; init; }

    /// <summary>True when it was already generated and nothing was spent this time.</summary>
    public bool ServedFromCache { get; init; }

    /// <summary>Builds the persistable entity. The caller saves it.</summary>
    public Walkthrough ToEntity(DateTimeOffset? now = null)
        => Walkthrough.Generate(SessionId, Level, BodyMarkdown, Charge.CostUsd, now);
}

/// <summary>One sentence against one milestone.</summary>
/// <param name="MilestoneId">The milestone annotated.</param>
/// <param name="Label">Its pane-1 label.</param>
/// <param name="Sentence">Exactly one sentence.</param>
public sealed record TeachingAnnotation(Guid MilestoneId, MilestoneLabel Label, string Sentence);

/// <summary>The inline annotations — one call over the whole milestone list.</summary>
public sealed record TeachingAnnotationResult : TeachingOutcome
{
    /// <summary>One entry per milestone the model answered for.</summary>
    public required IReadOnlyList<TeachingAnnotation> Annotations { get; init; }

    /// <summary>Writes the sentences onto the milestone entities. The caller saves them.</summary>
    public int ApplyTo(IEnumerable<Milestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        var byId = Annotations.ToDictionary(static annotation => annotation.MilestoneId);
        var applied = 0;
        foreach (var milestone in milestones)
        {
            if (byId.TryGetValue(milestone.Id, out var annotation))
            {
                milestone.Annotate(annotation.Sentence);
                applied++;
            }
        }

        return applied;
    }
}

/// <summary>An <em>explain this</em> answer, or the reason there is not one.</summary>
public sealed record TeachingExplanationResult : TeachingOutcome
{
    /// <summary>Whether the answer was produced.</summary>
    public required bool Answered { get; init; }

    /// <summary>The answer, when there is one.</summary>
    public string? BodyMarkdown { get; init; }

    /// <summary>
    /// What to show instead, when the daily cap is spent. Section 34.5: every limit message names
    /// who can raise it, because a dead end that does not say who to ask is the fastest way to make
    /// somebody stop using the tool.
    /// </summary>
    public string? CapMessage { get; init; }

    /// <summary>The allowance as it stood when this was asked.</summary>
    public ExplainThisAllowance? Allowance { get; init; }
}

/// <summary>Teaching, across section 13's three surfaces.</summary>
public interface ITeachingGenerator
{
    /// <summary>
    /// The walkthrough for a session, generating it only if it has not been generated already.
    /// </summary>
    Task<TeachingWalkthroughResult> GetWalkthroughAsync(
        TeachingRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Annotates the milestone list in a single call.</summary>
    Task<TeachingAnnotationResult> AnnotateMilestonesAsync(
        TeachingRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Answers one <em>explain this</em> click, subject to the per-user cap.</summary>
    Task<TeachingExplanationResult> ExplainAsync(
        ExplainThisRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Clears a reader's concept ledger. Section 13: people forget.</summary>
    Task ResetConceptLedgerAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Section 13's three surfaces, at ascending cost, all generated lazily.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here runs when a session ends.</strong> Every entry point is something a reader
/// asked for: they opened the tab, they clicked a milestone, they clicked <em>explain this</em>.
/// That is not an optimisation — section 13 makes laziness part of the cost model, because teaching
/// generated for sessions nobody reads is exactly the spend an admin notices and cuts, and teaching
/// is the line item with no immediately visible output to defend itself.
/// </para>
/// <para>
/// The concept ledger is the part that compounds. Every pass declares what it defined; those
/// concepts go into the reader's ledger; the next pass is told the reader already knows them and
/// references them instead of teaching them again. Over a dozen sessions an
/// <c>explain_everything</c> reader is reading something much closer to <c>skip_the_basics</c>
/// without ever having changed a setting — which is the behaviour that makes the feature worth
/// having, and it costs one paragraph of prompt.
/// </para>
/// </remarks>
public sealed class TeachingGenerator : ITeachingGenerator
{
    private readonly IModelClientFactory _clients;
    private readonly TeachingPromptBuilder _prompts;
    private readonly IConceptLedgerStore _concepts;
    private readonly IWalkthroughStore _walkthroughs;
    private readonly IExplainThisQuota _quota;
    private readonly TeachingOptions _options;
    private readonly ILogger<TeachingGenerator> _logger;

    /// <summary>Creates a generator.</summary>
    public TeachingGenerator(
        IModelClientFactory clients,
        TeachingPromptBuilder prompts,
        IConceptLedgerStore concepts,
        IWalkthroughStore walkthroughs,
        IExplainThisQuota quota,
        TeachingOptions options,
        ILogger<TeachingGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(concepts);
        ArgumentNullException.ThrowIfNull(walkthroughs);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _prompts = prompts;
        _concepts = concepts;
        _walkthroughs = walkthroughs;
        _quota = quota;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TeachingWalkthroughResult> GetWalkthroughAsync(
        TeachingRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        var level = request.EffectiveLevel;

        if (!request.Regenerate)
        {
            var existing = await _walkthroughs
                .FindAsync(request.Evidence.SessionId, level, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return new TeachingWalkthroughResult
                {
                    SessionId = request.Evidence.SessionId,
                    Level = level,
                    BodyMarkdown = existing.BodyMd,
                    ServedFromCache = true,
                    Usage = ModelUsage.Empty,
                    Charge = ModelCharge.None,
                };
            }
        }

        if (request.Evidence.IsEmpty)
        {
            // Nothing happened, so there is nothing grounded to say. Section 13's whole premise is
            // that the value comes from the transcript; spending tokens to write around an empty one
            // produces exactly the generic content it warns against.
            return new TeachingWalkthroughResult
            {
                SessionId = request.Evidence.SessionId,
                Level = level,
                BodyMarkdown =
                    "There is nothing recorded for this session yet, so there is nothing to explain. "
                    + "Come back once it has finished running.",
                Usage = ModelUsage.Empty,
                Charge = ModelCharge.None,
            };
        }

        var ledger = await LoadLedgerAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var completion = await CompleteAsync(
                TeachingSurface.Walkthrough,
                level,
                ledger,
                request.Evidence,
                TeachingCalibration.MaxOutputTokens(level, _options),
                TeachingSchema.NarrativeFormat,
                target: null,
                credential,
                cancellationToken)
            .ConfigureAwait(false);

        var payload = TeachingSchema.ParseNarrative(completion.StructuredJson ?? completion.Text);
        var scrub = TeachingToneGuard.Scrub(payload.BodyMarkdown);
        LogTone(scrub, request.Evidence.SessionId);

        var body = scrub.Text.Trim();
        if (body.Length == 0)
        {
            throw new TeachingException("The teaching model returned an empty walkthrough.");
        }

        var concepts = await RecordConceptsAsync(request.UserId, payload.ConceptsExplained, cancellationToken)
            .ConfigureAwait(false);

        var result = new TeachingWalkthroughResult
        {
            SessionId = request.Evidence.SessionId,
            Level = level,
            BodyMarkdown = body,
            ConceptsExplained = concepts,
            ToneStatementsRemoved = scrub.Removed,
            Usage = completion.Usage,
            Charge = completion.Charge,
        };

        await _walkthroughs.SaveAsync(result.ToEntity(), cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<TeachingAnnotationResult> AnnotateMilestonesAsync(
        TeachingRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        var level = request.EffectiveLevel;
        var milestones = request.Evidence.Milestones;

        if (milestones.Count == 0)
        {
            return new TeachingAnnotationResult
            {
                SessionId = request.Evidence.SessionId,
                Level = level,
                Annotations = [],
                Usage = ModelUsage.Empty,
                Charge = ModelCharge.None,
            };
        }

        var ledger = await LoadLedgerAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        // Section 13: one call over the milestone list. One call per milestone would multiply the
        // cheapest surface by the number of milestones and lose the only context that makes the
        // sentences read as a sequence rather than four unrelated captions.
        var completion = await CompleteAsync(
                TeachingSurface.MilestoneAnnotations,
                level,
                ledger,
                request.Evidence,
                _options.AnnotationMaxOutputTokens,
                TeachingSchema.AnnotationFormat,
                target: null,
                credential,
                cancellationToken)
            .ConfigureAwait(false);

        var payload = TeachingSchema.ParseAnnotations(completion.StructuredJson ?? completion.Text);
        var annotations = new List<TeachingAnnotation>();
        var removed = 0;

        foreach (var entry in payload.Annotations ?? [])
        {
            if (entry.Index < 0 || entry.Index >= milestones.Count)
            {
                continue;
            }

            var scrub = TeachingToneGuard.Scrub(entry.Sentence);
            removed += scrub.Removed;

            var sentence = FirstSentence(scrub.Text, _options.MaxAnnotationCharacters);
            if (sentence.Length == 0)
            {
                continue;
            }

            var milestone = milestones[entry.Index];
            annotations.Add(new TeachingAnnotation(milestone.Id, milestone.Label, sentence));
        }

        var concepts = await RecordConceptsAsync(request.UserId, payload.ConceptsExplained, cancellationToken)
            .ConfigureAwait(false);

        return new TeachingAnnotationResult
        {
            SessionId = request.Evidence.SessionId,
            Level = level,
            Annotations = annotations,
            ConceptsExplained = concepts,
            ToneStatementsRemoved = removed,
            Usage = completion.Usage,
            Charge = completion.Charge,
        };
    }

    /// <inheritdoc />
    public async Task<TeachingExplanationResult> ExplainAsync(
        ExplainThisRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        var level = request.EffectiveLevel;

        var allowance = await _quota
            .TryConsumeAsync(request.UserId, _options.ExplainThisPerUserPerDay, cancellationToken)
            .ConfigureAwait(false);

        if (!allowance.Allowed)
        {
            _logger.LogInformation(
                "Explain-this cap reached for user {UserId} ({Limit} per day); nothing was spent.",
                request.UserId,
                allowance.Limit);

            return new TeachingExplanationResult
            {
                SessionId = request.Evidence.SessionId,
                Level = level,
                Answered = false,
                Allowance = allowance,
                CapMessage =
                    $"You've used all {allowance.Limit} of today's explanations. They reset at "
                    + $"{allowance.ResetsAt:HH:mm} UTC. If you need more, {_options.CapEscalationContact} "
                    + "can raise the limit.",
                Usage = ModelUsage.Empty,
                Charge = ModelCharge.None,
            };
        }

        var ledger = await LoadLedgerAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var completion = await CompleteAsync(
                TeachingSurface.ExplainThis,
                level,
                ledger,
                request.Evidence,
                _options.ExplainThisMaxOutputTokens,
                TeachingSchema.NarrativeFormat,
                request.Target,
                credential,
                cancellationToken)
            .ConfigureAwait(false);

        var payload = TeachingSchema.ParseNarrative(completion.StructuredJson ?? completion.Text);
        var scrub = TeachingToneGuard.Scrub(payload.BodyMarkdown);
        LogTone(scrub, request.Evidence.SessionId);

        var concepts = await RecordConceptsAsync(request.UserId, payload.ConceptsExplained, cancellationToken)
            .ConfigureAwait(false);

        return new TeachingExplanationResult
        {
            SessionId = request.Evidence.SessionId,
            Level = level,
            Answered = true,
            BodyMarkdown = scrub.Text.Trim(),
            Allowance = allowance,
            ConceptsExplained = concepts,
            ToneStatementsRemoved = scrub.Removed,
            Usage = completion.Usage,
            Charge = completion.Charge,
        };
    }

    /// <inheritdoc />
    public Task ResetConceptLedgerAsync(Guid userId, CancellationToken cancellationToken = default)
        => _concepts.ResetAsync(userId, cancellationToken);

    private async Task<ConceptLedgerSnapshot> LoadLedgerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var entries = await _concepts.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        return ConceptLedgerSnapshot.From(entries, _options.ConceptInjectionLimit);
    }

    private async Task<ModelCompletion> CompleteAsync(
        TeachingSurface surface,
        TeachingLevel level,
        ConceptLedgerSnapshot ledger,
        TeachingEvidence evidence,
        int maxOutputTokens,
        ModelResponseFormat format,
        ExplainTarget? target,
        ModelCredential credential,
        CancellationToken cancellationToken)
    {
        var request = new ModelRequest
        {
            Model = _options.Model,
            SystemPrompt = _prompts.BuildSystemPrompt(surface, level, ledger, evidence),
            Messages = [ModelMessage.User(_prompts.BuildUserPrompt(surface, evidence, _options, target))],
            MaxOutputTokens = maxOutputTokens,
            Temperature = _options.Temperature,
            ResponseFormat = format,
            CorrelationId = evidence.SessionId.ToString(),
        };

        var client = _clients.GetClient(_options.Model);
        return await client.CompleteAsync(request, credential, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> RecordConceptsAsync(
        Guid userId,
        IReadOnlyList<string>? concepts,
        CancellationToken cancellationToken)
    {
        var cleaned = (concepts ?? [])
            .Where(static concept => !string.IsNullOrWhiteSpace(concept))
            .Select(ConceptLedgerSnapshot.Normalise)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (cleaned.Count == 0)
        {
            return [];
        }

        await _concepts.RecordAsync(userId, cleaned, cancellationToken).ConfigureAwait(false);
        return cleaned;
    }

    private void LogTone(TeachingToneScrub scrub, Guid sessionId)
    {
        if (scrub.Removed > 0)
        {
            _logger.LogWarning(
                "Teaching removed {Count} quiz or progress line(s) from the answer for session "
                + "{SessionId} (section 13: no quizzes, no progress bars, no streaks).",
                scrub.Removed,
                sessionId);
        }
    }

    private static string FirstSentence(string text, int maxCharacters)
    {
        var trimmed = text.ReplaceLineEndings(" ").Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var end = trimmed.AsSpan().IndexOfAny(['.', '!', '?']);
        if (end >= 0)
        {
            trimmed = trimmed[..(end + 1)];
        }

        return trimmed.Length <= maxCharacters ? trimmed : trimmed[..maxCharacters].TrimEnd() + "…";
    }
}
