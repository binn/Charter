using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Charter.Logging;

namespace Charter.Models;

/// <summary>
/// Wraps a control-plane model client so every call it makes is logged the way section 19 asks:
/// metadata always, transcript bodies only behind <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are the calls Charter's own HTTP client makes - refinement, teaching, recap (section 20b.1)
/// - and they carry exactly what section 19's leak warning is about: the system prompt Charter built
/// from repository content, the requester's description of their business problem, and the model's
/// answer. They are also the calls an operator most wants to see when a session goes wrong, and
/// until this existed Charter logged nothing about them at all: not the model, not the latency, not
/// the cost.
/// </para>
/// <para>
/// A decorator rather than a change to each client, because the interesting facts are all on the
/// request and the response and none of them are provider-specific. It wraps rather than replaces,
/// so <see cref="Inner"/> is still the client that did the work - which is what a caller asserting
/// on the transport wants to see.
/// </para>
/// </remarks>
public sealed class TranscriptLoggingModelClient : IModelClient
{
    /// <summary>The <see cref="TranscriptEvent.Type"/> a single-shot completion is logged under.</summary>
    public const string CompletionEventType = "model_call";

    /// <summary>The <see cref="TranscriptEvent.Type"/> a streamed completion is logged under.</summary>
    public const string StreamEventType = "model_stream";

    private readonly IModelClient _inner;
    private readonly ITranscriptLog _transcripts;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public TranscriptLoggingModelClient(IModelClient inner, ITranscriptLog transcripts)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(transcripts);

        _inner = inner;
        _transcripts = transcripts;
    }

    /// <summary>The client that actually speaks to the provider.</summary>
    public IModelClient Inner => _inner;

    /// <inheritdoc />
    public ModelProvider Provider => _inner.Provider;

    /// <inheritdoc />
    public IReadOnlyCollection<ModelProvider> SupportedProviders => _inner.SupportedProviders;

    /// <inheritdoc />
    public bool Supports(ModelIdentifier model) => _inner.Supports(model);

    /// <inheritdoc />
    public async Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var completion = await _inner.CompleteAsync(request, credential, cancellationToken)
                .ConfigureAwait(false);

            _transcripts.Record(Describe(
                CompletionEventType,
                request,
                completion,
                Stopwatch.GetElapsedTime(started),
                outcome: null));

            return completion;
        }
        catch (Exception ex)
        {
            _transcripts.Record(Describe(
                CompletionEventType,
                request,
                completion: null,
                Stopwatch.GetElapsedTime(started),
                Outcome(ex)));
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        ModelCredential credential,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = Stopwatch.GetTimestamp();
        ModelCompletion? completion = null;

        // The enumerator is disposed however the stream ends - drained, abandoned, or thrown out of -
        // so the log line is written exactly once either way, and a stream nobody finished reading is
        // still accounted for.
        var enumerator = _inner.StreamAsync(request, credential, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        string? outcome = null;

        try
        {
            while (true)
            {
                ModelStreamEvent current;

                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    outcome = Outcome(ex);
                    throw;
                }

                if (current is ModelStreamEvent.Completed completed)
                {
                    completion = completed.Completion;
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);

            _transcripts.Record(Describe(
                StreamEventType,
                request,
                completion,
                Stopwatch.GetElapsedTime(started),
                outcome ?? (completion is null ? "incomplete" : null)));
        }
    }

    /// <summary>
    /// Turns a call into the section 19 shape: facts about the work, and separately the work itself.
    /// </summary>
    internal static TranscriptEvent Describe(
        string type,
        ModelRequest request,
        ModelCompletion? completion,
        TimeSpan duration,
        string? outcome)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TranscriptEvent
        {
            Type = type,
            CorrelationId = request.CorrelationId,
            Model = (completion?.Model ?? request.Model).ToString(),
            Duration = duration,
            InputTokens = completion?.Usage.TotalInputTokens,
            OutputTokens = completion?.Usage.OutputTokens,

            // Section 34.1: a subscription-backed call is not free, it is notional. Reporting the
            // metered zero would make every pooled session look costless in the logs.
            CostUsd = completion is null
                ? null
                : completion.Charge.Unit == ModelChargeUnit.Usd
                    ? completion.Charge.CostUsd
                    : completion.Charge.NotionalCostUsd,
            Outcome = outcome,
            Body = Body(request, completion),
        };
    }

    /// <summary>
    /// The prompt and the answer, as one blob. Repository content and requester context both live
    /// here, which is why nothing reads it unless the operator asked for it.
    /// </summary>
    private static string Body(ModelRequest request, ModelCompletion? completion)
    {
        var text = new StringBuilder();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            text.Append("system: ").AppendLine(request.SystemPrompt);
        }

        foreach (var message in request.Messages)
        {
            text.Append(CultureInfo.InvariantCulture, $"{message.Role.ToString().ToLowerInvariant()}: ")
                .AppendLine(message.Content);
        }

        if (completion is not null)
        {
            text.Append("response: ").AppendLine(completion.Text);
        }

        return text.ToString();
    }

    private static string Outcome(Exception exception) => exception switch
    {
        ModelRateLimitException => "rate_limited",
        ModelAuthenticationException => "rejected",
        ModelClientException => "provider_error",
        OperationCanceledException => "cancelled",
        _ => "failed",
    };
}
