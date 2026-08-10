using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>
/// Every provider that exposes <c>/chat/completions</c>: OpenAI, OpenRouter, xAI/Grok, DeepSeek,
/// Groq, Azure OpenAI, Ollama, and any self-hosted gateway behind a per-credential
/// <see cref="ModelCredential.BaseUrl"/>. Section 20b.1 and section 20b.2.
/// </summary>
public sealed class OpenAiCompatibleModelClient : IModelClient
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves.</summary>
    public const string HttpClientName = "charter-openai-compatible";

    private static readonly ModelProvider[] Providers =
    [
        ModelProvider.OpenAi,
        ModelProvider.OpenRouter,
        ModelProvider.XAi,
        ModelProvider.DeepSeek,
        ModelProvider.Groq,
        ModelProvider.AzureOpenAi,
        ModelProvider.Ollama,
        ModelProvider.OpenAiCompatible,
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModelClientOptions _options;
    private readonly IModelCostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenAiCompatibleModelClient> _logger;

    /// <summary>Creates a client.</summary>
    public OpenAiCompatibleModelClient(
        IHttpClientFactory httpClientFactory,
        ModelClientOptions options,
        IModelCostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<OpenAiCompatibleModelClient> logger)
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
    public ModelProvider Provider => ModelProvider.OpenAiCompatible;

    /// <inheritdoc />
    public IReadOnlyCollection<ModelProvider> SupportedProviders => Providers;

    /// <inheritdoc />
    public bool Supports(ModelIdentifier model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Array.IndexOf(Providers, model.Provider) >= 0;
    }

    /// <inheritdoc />
    public async Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var httpClient = CreateHttpClient();
        using var message = BuildRequest(request, credential, stream: false);

        using var response = await SendAsync(httpClient, message, request, credential, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body, request.Model.Provider);

        var root = document.RootElement;
        var text = new StringBuilder();
        var stopReason = ModelStopReason.Unknown;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var messageElement)
                    && messageElement.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                }

                if (choice.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    stopReason = MapStopReason(finish.GetString());
                }
            }
        }

        var usage = ReadUsage(root);
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

        using var httpClient = CreateHttpClient();
        using var message = BuildRequest(request, credential, stream: true);

        using var response = await SendAsync(
                httpClient,
                message,
                request,
                credential,
                cancellationToken,
                HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var text = new StringBuilder();
            var usage = ModelUsage.Empty;
            var stopReason = ModelStopReason.Unknown;

            await foreach (var frame in ServerSentEventReader.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false))
            {
                if (frame.Length == 0 || string.Equals(frame, "[DONE]", StringComparison.Ordinal))
                {
                    continue;
                }

                using var document = ParseJson(frame, request.Model.Provider);
                var root = document.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("finish_reason", out var finish)
                            && finish.ValueKind == JsonValueKind.String)
                        {
                            stopReason = MapStopReason(finish.GetString());
                        }

                        if (!choice.TryGetProperty("delta", out var delta))
                        {
                            continue;
                        }

                        if (delta.TryGetProperty("reasoning", out var reasoning)
                            && reasoning.ValueKind == JsonValueKind.String
                            && reasoning.GetString() is { Length: > 0 } reasoningText)
                        {
                            yield return new ModelStreamEvent.ReasoningDelta(reasoningText);
                        }

                        if (delta.TryGetProperty("content", out var content)
                            && content.ValueKind == JsonValueKind.String
                            && content.GetString() is { Length: > 0 } chunk)
                        {
                            text.Append(chunk);
                            yield return new ModelStreamEvent.TextDelta(chunk);
                        }
                    }
                }

                var frameUsage = ReadUsage(root);
                if (frameUsage is not null)
                {
                    usage = frameUsage;
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
    }

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = _options.RequestTimeout;
        return client;
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, ModelCredential credential, bool stream)
    {
        var baseUrl = _options.ResolveBaseUrl(credential, request.Model.Provider);
        var endpoint = new Uri(EnsureTrailingSlash(baseUrl), "chat/completions");

        var body = new JsonObject
        {
            ["model"] = request.Model.Model,
            ["messages"] = BuildMessages(request),
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = stream,
        };

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops)
            {
                array.Add(stop);
            }

            body["stop"] = array;
        }

        if (request.ResponseFormat is { } format)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = format.Name,
                    ["strict"] = format.Strict,
                    ["schema"] = JsonNode.Parse(format.JsonSchema),
                },
            };
        }

        if (stream)
        {
            // Without this, several providers omit usage entirely on a streamed response, and
            // section 20b.5's ledger would have nothing to settle against.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.Model.Provider == ModelProvider.OpenRouter)
        {
            // Section 20b.6: OpenRouter reports the actual cost of the generation when asked, and a
            // reported cost always beats an estimate.
            body["usage"] = new JsonObject { ["include"] = true };
        }

        if (request.CorrelationId is { Length: > 0 } correlationId)
        {
            body["user"] = correlationId;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        ApplyAuthentication(message, credential, request.Model.Provider);
        return message;
    }

    private void ApplyAuthentication(
        HttpRequestMessage message,
        ModelCredential credential,
        ModelProvider provider)
    {
        var secret = credential.Secret.Reveal();

        if (provider == ModelProvider.AzureOpenAi)
        {
            // Azure OpenAI authenticates with its own header rather than a bearer token.
            message.Headers.TryAddWithoutValidation("api-key", secret);
        }
        else if (secret.Length > 0)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        if (provider == ModelProvider.OpenRouter)
        {
            if (_options.OpenRouterReferer is { } referer)
            {
                message.Headers.TryAddWithoutValidation("HTTP-Referer", referer.ToString());
            }

            message.Headers.TryAddWithoutValidation("X-Title", _options.OpenRouterTitle);
        }
    }

    private static JsonArray BuildMessages(ModelRequest request)
    {
        var messages = new JsonArray();

        if (request.SystemPrompt is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var turn in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role == ModelRole.Assistant ? "assistant" : "user",
                ["content"] = turn.Content,
            });
        }

        return messages;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage message,
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, completionOption, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelClientException(
                $"Could not reach the {request.Model.Provider} endpoint.",
                request.Model.Provider,
                null,
                ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ModelHttpFailure.Create(
                request.Model.Provider,
                response.StatusCode,
                response.Headers,
                body,
                credential.Secret,
                _timeProvider.GetUtcNow(),
                _logger);
        }
    }

    private async Task<ModelCompletion> BuildCompletionAsync(
        ModelRequest request,
        ModelCredential credential,
        string text,
        ModelStopReason stopReason,
        ModelUsage? usage,
        CancellationToken cancellationToken)
    {
        var resolvedUsage = usage ?? ModelUsage.Empty;
        var charge = await _costCalculator
            .CalculateAsync(request.Model, resolvedUsage, credential, cancellationToken)
            .ConfigureAwait(false);

        return new ModelCompletion
        {
            Model = request.Model,
            Text = text,
            StructuredJson = request.ResponseFormat is null ? null : text,
            StopReason = stopReason,
            Usage = resolvedUsage,
            Charge = charge,
        };
    }

    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cachedTokens = 0L;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var cached))
        {
            cachedTokens = ReadInt64(cached);
        }

        var promptTokens = usage.TryGetProperty("prompt_tokens", out var prompt) ? ReadInt64(prompt) : 0L;
        var completionTokens =
            usage.TryGetProperty("completion_tokens", out var completion) ? ReadInt64(completion) : 0L;

        decimal? reportedCost = null;
        if (usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number)
        {
            reportedCost = cost.GetDecimal();
        }

        return new ModelUsage
        {
            // Providers report prompt_tokens inclusive of the cached portion; splitting them keeps
            // the cost calculation from charging cache reads at the full input rate.
            InputTokens = Math.Max(0, promptTokens - cachedTokens),
            OutputTokens = completionTokens,
            CacheReadInputTokens = cachedTokens,
            ProviderReportedCostUsd = reportedCost,
        };
    }

    private static long ReadInt64(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.String when long.TryParse(
            element.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => 0L,
    };

    private static ModelStopReason MapStopReason(string? finishReason) => finishReason switch
    {
        "stop" => ModelStopReason.EndTurn,
        "length" => ModelStopReason.MaxTokens,
        "content_filter" => ModelStopReason.Refusal,
        _ => ModelStopReason.Unknown,
    };

    private static JsonDocument ParseJson(string body, ModelProvider provider)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The body may echo the credential back; it never reaches the message.
            throw new ModelClientException(
                $"The {provider} endpoint returned a response that was not valid JSON.",
                provider,
                HttpStatusCode.OK,
                ex);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
