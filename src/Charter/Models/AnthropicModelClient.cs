using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using AnthropicMessageRole = Anthropic.Models.Messages.Role;

namespace Charter.Models;

/// <summary>
/// Anthropic, over the official <c>Anthropic</c> NuGet package (section 3).
/// </summary>
/// <remarks>
/// <para>
/// The SDK is configured with retries off: section 20b.4 requires a <c>429</c> to exhaust the grant
/// and be recorded, not to be silently retried behind Charter's back.
/// </para>
/// <para>
/// The SDK's exceptions do not carry response headers, so the request runs inside a
/// <see cref="ModelHttpDiagnosticsScope"/> and the reset header is read back from there. See
/// <see cref="ModelHttpDiagnostics"/> for why.
/// </para>
/// </remarks>
public sealed class AnthropicModelClient : IModelClient
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves.</summary>
    public const string HttpClientName = "charter-anthropic";

    private static readonly ModelProvider[] Providers = [ModelProvider.Anthropic];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModelClientOptions _options;
    private readonly IModelCostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnthropicModelClient> _logger;

    /// <summary>Creates a client.</summary>
    public AnthropicModelClient(
        IHttpClientFactory httpClientFactory,
        ModelClientOptions options,
        IModelCostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<AnthropicModelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(costCalculator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _costCalculator = costCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public ModelProvider Provider => ModelProvider.Anthropic;

    /// <inheritdoc />
    public IReadOnlyCollection<ModelProvider> SupportedProviders => Providers;

    /// <inheritdoc />
    public bool Supports(ModelIdentifier model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Provider == ModelProvider.Anthropic;
    }

    /// <inheritdoc />
    public async Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var scope = new ModelHttpDiagnosticsScope();
        var client = CreateClient(credential);

        Message message;
        try
        {
            message = await client.Messages
                .Create(BuildParameters(request), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AnthropicException ex)
        {
            throw Translate(ex, scope.Diagnostics, credential);
        }

        var text = new StringBuilder();
        var stopReason = ModelStopReason.Unknown;

        if (message.Content is { } blocks)
        {
            foreach (var block in blocks)
            {
                if (block.TryPickText(out var textBlock) && textBlock.Text is { Length: > 0 } value)
                {
                    text.Append(value);
                }
            }
        }

        if (message.StopReason is { } reason)
        {
            stopReason = MapStopReason(reason.Raw());
        }

        var usage = ReadUsage(message.Usage);
        return await BuildCompletionAsync(request, credential, text.ToString(), stopReason, usage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        ModelCredential credential,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var scope = new ModelHttpDiagnosticsScope();
        var client = CreateClient(credential);

        var text = new StringBuilder();
        var usage = ModelUsage.Empty;
        var stopReason = ModelStopReason.Unknown;

        var events = client.Messages.CreateStreaming(BuildParameters(request), cancellationToken);
        await using var enumerator = events.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (AnthropicException ex)
            {
                throw Translate(ex, scope.Diagnostics, credential);
            }

            if (!moved)
            {
                break;
            }

            var @event = enumerator.Current;

            if (@event.TryPickStart(out var start) && start.Message?.Usage is { } startUsage)
            {
                usage = ModelUsage.Add(usage, ReadUsage(startUsage));
            }

            if (@event.TryPickContentBlockDelta(out var blockDelta))
            {
                if (blockDelta.Delta.TryPickText(out var textDelta)
                    && textDelta.Text is { Length: > 0 } chunk)
                {
                    text.Append(chunk);
                    yield return new ModelStreamEvent.TextDelta(chunk);
                }
                else if (blockDelta.Delta.TryPickThinking(out var thinkingDelta)
                    && thinkingDelta.Thinking is { Length: > 0 } reasoning)
                {
                    yield return new ModelStreamEvent.ReasoningDelta(reasoning);
                }
            }

            if (@event.TryPickDelta(out var messageDelta))
            {
                if (messageDelta.Usage is { } deltaUsage)
                {
                    usage = usage with { OutputTokens = deltaUsage.OutputTokens };
                }

                if (messageDelta.Delta?.StopReason is { } reason)
                {
                    stopReason = MapStopReason(reason.Raw());
                }
            }
        }

        var completion = await BuildCompletionAsync(
                request,
                credential,
                text.ToString(),
                stopReason,
                usage,
                cancellationToken)
            .ConfigureAwait(false);

        yield return new ModelStreamEvent.Completed(completion);
    }

    private AnthropicClient CreateClient(ModelCredential credential)
    {
        var options = new ClientOptions
        {
            HttpClient = _httpClientFactory.CreateClient(HttpClientName),
            BaseUrl = _options.ResolveBaseUrl(credential, ModelProvider.Anthropic).AbsoluteUri.TrimEnd('/'),
            // Section 20b.4: do not blind-retry. A 429 is a decision for the resolver, not the SDK.
            MaxRetries = 0,
            Timeout = _options.RequestTimeout,
        };

        // A subscription grant carries an OAuth access token, which goes on Authorization: Bearer.
        // An API-key grant goes on x-api-key. The SDK picks the header from which one is set.
        if (credential.IsSubscription)
        {
            options = options with { AuthToken = credential.Secret.Reveal() };
        }
        else
        {
            options = options with { ApiKey = credential.Secret.Reveal() };
        }

        return new AnthropicClient(options);
    }

    private MessageCreateParams BuildParameters(ModelRequest request)
    {
        var messages = new List<MessageParam>(request.Messages.Count);
        foreach (var turn in request.Messages)
        {
            messages.Add(new MessageParam
            {
                Role = turn.Role == ModelRole.Assistant
                    ? AnthropicMessageRole.Assistant
                    : AnthropicMessageRole.User,
                Content = turn.Content,
            });
        }

        var parameters = new MessageCreateParams
        {
            Model = request.Model.Model,
            MaxTokens = request.MaxOutputTokens,
            Messages = messages,
        };

        if (request.SystemPrompt is { Length: > 0 } system)
        {
            parameters = parameters with { System = system };
        }

        // ModelRequest.Temperature is deliberately dropped here. Anthropic models released after
        // Claude Opus 4.6 reject any value other than the default with a 400, and the SDK marks the
        // parameter obsolete. Silently sending it would turn a portable request into a hard failure
        // on exactly the models section 4.2 defaults to.
        if (request.StopSequences is { Count: > 0 } stops)
        {
            parameters = parameters with { StopSequences = stops.ToList() };
        }

        if (request.ResponseFormat is { } format)
        {
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(format.JsonSchema);
            if (schema is not null)
            {
                parameters = parameters with
                {
                    OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = schema } },
                };
            }
        }

        if (request.CorrelationId is { Length: > 0 } correlationId)
        {
            parameters = parameters with { Metadata = new Metadata { UserID = correlationId } };
        }

        return parameters;
    }

    private async Task<ModelCompletion> BuildCompletionAsync(
        ModelRequest request,
        ModelCredential credential,
        string text,
        ModelStopReason stopReason,
        ModelUsage usage,
        CancellationToken cancellationToken)
    {
        var charge = await _costCalculator
            .CalculateAsync(request.Model, usage, credential, cancellationToken)
            .ConfigureAwait(false);

        return new ModelCompletion
        {
            Model = request.Model,
            Text = text,
            StructuredJson = request.ResponseFormat is null ? null : text,
            StopReason = stopReason,
            Usage = usage,
            Charge = charge,
        };
    }

    private ModelClientException Translate(
        AnthropicException exception,
        ModelHttpDiagnostics diagnostics,
        ModelCredential credential)
    {
        var status = (exception as AnthropicApiException)?.StatusCode ?? diagnostics.StatusCode;

        if (status is null)
        {
            return new ModelClientException(
                "The Anthropic endpoint could not be reached.",
                ModelProvider.Anthropic,
                null,
                exception);
        }

        var body = (exception as AnthropicApiException)?.ResponseBody;
        return ModelHttpFailure.Create(
            ModelProvider.Anthropic,
            status.Value,
            diagnostics.Headers,
            body,
            credential.Secret,
            _timeProvider.GetUtcNow(),
            _logger,
            exception);
    }

    private static ModelUsage ReadUsage(Usage? usage)
    {
        if (usage is null)
        {
            return ModelUsage.Empty;
        }

        return new ModelUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadInputTokens = usage.CacheReadInputTokens ?? 0,
            CacheWriteInputTokens = usage.CacheCreationInputTokens ?? 0,
        };
    }

    private static ModelStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" => ModelStopReason.EndTurn,
        "max_tokens" => ModelStopReason.MaxTokens,
        "stop_sequence" => ModelStopReason.StopSequence,
        "refusal" => ModelStopReason.Refusal,
        _ => ModelStopReason.Unknown,
    };
}
