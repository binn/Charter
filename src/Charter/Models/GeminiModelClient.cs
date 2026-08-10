using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>
/// Google Gemini's native API. Section 20b.1: Gemini also exposes an OpenAI-compatible endpoint, but
/// the native client is preferred for the features the shim does not cover - notably native
/// structured output through <c>responseSchema</c> and Gemini's own cached-token accounting.
/// </summary>
public sealed class GeminiModelClient : IModelClient
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves.</summary>
    public const string HttpClientName = "charter-gemini";

    private static readonly ModelProvider[] Providers = [ModelProvider.Google];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModelClientOptions _options;
    private readonly IModelCostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GeminiModelClient> _logger;

    /// <summary>Creates a client.</summary>
    public GeminiModelClient(
        IHttpClientFactory httpClientFactory,
        ModelClientOptions options,
        IModelCostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<GeminiModelClient> logger)
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
    public ModelProvider Provider => ModelProvider.Google;

    /// <inheritdoc />
    public IReadOnlyCollection<ModelProvider> SupportedProviders => Providers;

    /// <inheritdoc />
    public bool Supports(ModelIdentifier model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Provider == ModelProvider.Google;
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

        using var response = await SendAsync(httpClient, message, credential, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body);

        var text = new StringBuilder();
        var stopReason = AppendCandidates(document.RootElement, text, ModelStopReason.Unknown);

        var usage = ReadUsage(document.RootElement) ?? ModelUsage.Empty;
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
                credential,
                cancellationToken,
                HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var text = new StringBuilder();
            var stopReason = ModelStopReason.Unknown;
            var usage = ModelUsage.Empty;

            await foreach (var frame in ServerSentEventReader.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false))
            {
                if (frame.Length == 0)
                {
                    continue;
                }

                using var document = ParseJson(frame);
                var chunk = new StringBuilder();
                stopReason = AppendCandidates(document.RootElement, chunk, stopReason);

                if (chunk.Length > 0)
                {
                    var delta = chunk.ToString();
                    text.Append(delta);
                    yield return new ModelStreamEvent.TextDelta(delta);
                }

                // Gemini repeats cumulative usage on every chunk; the last one wins.
                var frameUsage = ReadUsage(document.RootElement);
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
        var baseUrl = _options.ResolveBaseUrl(credential, ModelProvider.Google);
        var method = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        var endpoint = new Uri(EnsureTrailingSlash(baseUrl), $"models/{request.Model.Model}:{method}");

        var contents = new JsonArray();
        foreach (var turn in request.Messages)
        {
            contents.Add(new JsonObject
            {
                // Gemini names the assistant side "model".
                ["role"] = turn.Role == ModelRole.Assistant ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.Content }),
            });
        }

        var generationConfig = new JsonObject
        {
            ["maxOutputTokens"] = request.MaxOutputTokens,
        };

        if (request.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops)
            {
                array.Add(stop);
            }

            generationConfig["stopSequences"] = array;
        }

        if (request.ResponseFormat is { } format)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseSchema"] = JsonNode.Parse(format.JsonSchema);
        }

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };

        if (request.SystemPrompt is { Length: > 0 } system)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            };
        }

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // The key goes in a header, never in the query string: a URL ends up in access logs, traces
        // and exception messages, and section 20b.2 forbids all three.
        message.Headers.TryAddWithoutValidation("x-goog-api-key", credential.Secret.Reveal());
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage message,
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
                "Could not reach the Gemini endpoint.",
                ModelProvider.Google,
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
                ModelProvider.Google,
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

    private static ModelStopReason AppendCandidates(
        JsonElement root,
        StringBuilder text,
        ModelStopReason stopReason)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return stopReason;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.TryGetProperty("finishReason", out var finish)
                && finish.ValueKind == JsonValueKind.String)
            {
                stopReason = MapStopReason(finish.GetString());
            }

            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                {
                    text.Append(partText.GetString());
                }
            }
        }

        return stopReason;
    }

    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var prompt = usage.TryGetProperty("promptTokenCount", out var p) ? ReadInt64(p) : 0L;
        var candidates = usage.TryGetProperty("candidatesTokenCount", out var c) ? ReadInt64(c) : 0L;
        var cached = usage.TryGetProperty("cachedContentTokenCount", out var cc) ? ReadInt64(cc) : 0L;

        return new ModelUsage
        {
            InputTokens = Math.Max(0, prompt - cached),
            OutputTokens = candidates,
            CacheReadInputTokens = cached,
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
        "STOP" => ModelStopReason.EndTurn,
        "MAX_TOKENS" => ModelStopReason.MaxTokens,
        "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" => ModelStopReason.Refusal,
        _ => ModelStopReason.Unknown,
    };

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ModelClientException(
                "The Gemini endpoint returned a response that was not valid JSON.",
                ModelProvider.Google,
                HttpStatusCode.OK,
                ex);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
