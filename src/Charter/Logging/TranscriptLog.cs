using System.Globalization;
using Charter.Configuration;
using Microsoft.Extensions.Logging;

namespace Charter.Logging;

/// <summary>
/// One thing that happened during a session, described so that it can be logged without exporting
/// the repository (section 19).
/// </summary>
/// <remarks>
/// The split between the metadata properties and <see cref="Body"/> is the whole point of this type.
/// Everything above <see cref="Body"/> is a fact <em>about</em> the work - what kind of call it was,
/// how long it took, what it cost, which files it touched - and section 19 says to log all of it by
/// default. <see cref="Body"/> is the work itself: prompts, completions, diffs, repository content
/// and the requester's business context. That only reaches a sink behind
/// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c>.
/// </remarks>
public sealed record TranscriptEvent
{
    /// <summary>What kind of event this is - <c>model_call</c>, <c>refine</c>, and so on.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The session, request or conversation this belongs to. Section 19: correlate everything by
    /// session id, so one query pulls the whole story.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>The model that served the call, where one did.</summary>
    public string? Model { get; init; }

    /// <summary>How long the call took.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Prompt tokens, cached and uncached.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Generated tokens.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>What the call cost, in USD, metered or notional.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// Repository paths the event concerns. Section 19 counts a path as metadata rather than as
    /// content, so these are logged even when bodies are withheld.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>How the call ended, when it did not simply succeed.</summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// The transcript itself. Withheld from every sink unless
    /// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c> is set.
    /// </summary>
    public string? Body { get; init; }
}

/// <summary>
/// The one place transcript content is allowed to reach the logging pipeline (section 19).
/// </summary>
/// <remarks>
/// <para>
/// Section 19's leak warning is unambiguous: transcripts contain repository content and requester
/// business context, so a transcript in a structured log property has exported source code into the
/// operator's log platform. Metadata is logged by default; bodies only behind
/// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c>, and the warning applies to every sink equally, which is
/// why the decision is made here rather than per sink.
/// </para>
/// <para>
/// Routing every transcript-shaped log line through one service is what makes that switch mean
/// something. Before this existed, the flag parsed, validated, reached <c>StartupOptions</c>, and
/// stopped - the safe default held only because nothing logged transcript bodies at all, which also
/// meant turning the flag on did nothing for the operator who set it.
/// </para>
/// </remarks>
public interface ITranscriptLog
{
    /// <summary>
    /// Whether transcript bodies reach the sinks. False is the section 4.2 default.
    /// </summary>
    bool BodiesIncluded { get; }

    /// <summary>Logs <paramref name="event"/>, with its body only if <see cref="BodiesIncluded"/>.</summary>
    void Record(TranscriptEvent @event);
}

/// <inheritdoc cref="ITranscriptLog" />
public sealed class TranscriptLog : ITranscriptLog
{
    /// <summary>
    /// The line printed at startup when bodies are being logged, so an operator who set the flag -
    /// or inherited an instance from someone who did - sees it in the same place they see the port.
    /// </summary>
    public const string LeakWarning =
        "CHARTER_LOG_INCLUDE_TRANSCRIPTS is on: transcript bodies - prompts, completions and the "
        + "repository content and business context inside them - are written to every enabled log "
        + "sink, including Seq and any OTLP collector. Source code leaves this instance for your log "
        + "platform (section 19). Turn it off once you have finished debugging.";

    private readonly ILogger _logger;

    /// <summary>Creates a transcript log over the section 4.2 logging block.</summary>
    public TranscriptLog(ILogger<TranscriptLog> logger, StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        BodiesIncluded = options.IncludeTranscripts;
    }

    private TranscriptLog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        BodiesIncluded = false;
    }

    /// <inheritdoc />
    public bool BodiesIncluded { get; }

    /// <summary>
    /// A transcript log that never emits a body, for a service graph assembled without the section
    /// 4.2 logging block.
    /// </summary>
    /// <remarks>
    /// The fallback is the safe position rather than the convenient one: a graph that forgot to
    /// register the real thing withholds repository content instead of exporting it.
    /// </remarks>
    public static ITranscriptLog MetadataOnly(ILogger logger) => new TranscriptLog(logger);

    /// <inheritdoc />
    public void Record(TranscriptEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Section 19: metadata by default. Information rather than Debug, because CHARTER_LOG_LEVEL
        // defaults to information and a cost figure nobody can see is not instrumentation.
        if (!BodiesIncluded || @event.Body is null)
        {
            _logger.LogInformation(
                "Transcript {TranscriptType} {TranscriptModel} for {TranscriptCorrelationId}: "
                + "{TranscriptOutcome} in {TranscriptDurationMs}ms, {TranscriptInputTokens} in / "
                + "{TranscriptOutputTokens} out, {TranscriptCostUsd} USD, paths {TranscriptPaths}",
                @event.Type,
                @event.Model ?? "(none)",
                @event.CorrelationId ?? "(uncorrelated)",
                @event.Outcome ?? "ok",
                Milliseconds(@event.Duration),
                @event.InputTokens,
                @event.OutputTokens,
                Money(@event.CostUsd),
                @event.Paths ?? []);

            return;
        }

        _logger.LogInformation(
            "Transcript {TranscriptType} {TranscriptModel} for {TranscriptCorrelationId}: "
            + "{TranscriptOutcome} in {TranscriptDurationMs}ms, {TranscriptInputTokens} in / "
            + "{TranscriptOutputTokens} out, {TranscriptCostUsd} USD, paths {TranscriptPaths}; "
            + "transcript {TranscriptBody}",
            @event.Type,
            @event.Model ?? "(none)",
            @event.CorrelationId ?? "(uncorrelated)",
            @event.Outcome ?? "ok",
            Milliseconds(@event.Duration),
            @event.InputTokens,
            @event.OutputTokens,
            Money(@event.CostUsd),
            @event.Paths ?? [],
            @event.Body);
    }

    private static double? Milliseconds(TimeSpan? duration)
        => duration is { } value ? Math.Round(value.TotalMilliseconds, 1) : null;

    private static string? Money(decimal? amount)
        => amount is { } value ? value.ToString("0.######", CultureInfo.InvariantCulture) : null;
}
