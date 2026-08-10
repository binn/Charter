using System.Net;
using System.Text.Json;
using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Streaming assembly, request shaping and usage capture for the two HTTP clients. No test here
/// makes a real network call - every response comes from <see cref="StubHttpMessageHandler"/>.
/// </summary>
public class ModelStreamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri BaseUrl = new("https://provider.test/v1/");

    [Fact]
    public async Task StreamedDeltasAssembleIntoOneCompletionWithUsage()
    {
        var handler = new StubHttpMessageHandler().EnqueueSse(
        [
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"The "}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"refined "}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"spec."},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":340,"prompt_tokens_details":{"cached_tokens":200}}}""",
            "[DONE]",
        ]);

        var client = OpenAiClient(handler);
        var events = new List<ModelStreamEvent>();

        await foreach (var @event in client.StreamAsync(
            Request(ModelIdentifier.Parse("openai/gpt-5")),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        var deltas = events.OfType<ModelStreamEvent.TextDelta>().Select(d => d.Text).ToList();
        Assert.Equal(["The ", "refined ", "spec."], deltas);

        var completed = Assert.IsType<ModelStreamEvent.Completed>(events[^1]);
        Assert.Equal("The refined spec.", completed.Completion.Text);
        Assert.Equal(ModelStopReason.EndTurn, completed.Completion.StopReason);
        Assert.Equal(1000, completed.Completion.Usage.InputTokens);
        Assert.Equal(200, completed.Completion.Usage.CacheReadInputTokens);
        Assert.Equal(340, completed.Completion.Usage.OutputTokens);
        Assert.Equal(1200, completed.Completion.Usage.TotalInputTokens);
    }

    [Fact]
    public async Task ReasoningDeltasAreSurfacedSeparatelyFromResponseText()
    {
        var handler = new StubHttpMessageHandler().EnqueueSse(
        [
            """{"choices":[{"index":0,"delta":{"reasoning":"weighing options"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":"stop"}]}""",
            "[DONE]",
        ]);

        var completion = await CollectAsync(OpenAiClient(handler), ModelIdentifier.Parse("openai/gpt-5"));

        Assert.Equal("answer", completion.Text);
    }

    [Fact]
    public async Task StreamRequestsAskForUsageSoTheLedgerHasSomethingToSettleAgainst()
    {
        var handler = new StubHttpMessageHandler().EnqueueSse(["[DONE]"]);

        await CollectAsync(OpenAiClient(handler), ModelIdentifier.Parse("openai/gpt-5"));

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean());
    }

    [Fact]
    public async Task AnOpenRouterRequestKeepsTheNestedModelNameAndAsksForCost()
    {
        var handler = new StubHttpMessageHandler().EnqueueSse(["[DONE]"]);

        await CollectAsync(
            OpenAiClient(handler),
            ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1"),
            ModelCredentialKind.OpenRouterKey);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("deepseek/deepseek-r1", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("include").GetBoolean());
    }

    [Fact]
    public async Task TheSystemPromptIsSentAsTheLeadingSystemTurn()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");

        var request = new ModelRequest
        {
            Model = ModelIdentifier.Parse("openai/gpt-5"),
            SystemPrompt = "You refine specs.",
            Messages =
            [
                ModelMessage.User("first"),
                ModelMessage.Assistant("clarifying question"),
                ModelMessage.User("second"),
            ],
        };

        await OpenAiClient(handler).CompleteAsync(
            request,
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(4, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
    }

    [Fact]
    public async Task StructuredOutputIsRequestedAsAJsonSchemaAndReturnedSeparately()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"choices":[{"message":{"content":"{\"title\":\"x\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""");

        var request = new ModelRequest
        {
            Model = ModelIdentifier.Parse("openai/gpt-5"),
            Messages = [ModelMessage.User("refine")],
            ResponseFormat = new ModelResponseFormat(
                "refined_spec",
                """{"type":"object","properties":{"title":{"type":"string"}}}"""),
        };

        var completion = await OpenAiClient(handler).CompleteAsync(
            request,
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("refined_spec", format.GetProperty("json_schema").GetProperty("name").GetString());

        Assert.Equal("""{"title":"x"}""", completion.StructuredJson);
    }

    [Fact]
    public async Task ProviderReportedCostBeatsAnEstimate()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"},{"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":1000,"cost":0.0425}}""");

        var completion = await OpenAiClient(handler).CompleteAsync(
            Request(ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1")),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenRouterKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCostBasis.ProviderReported, completion.Charge.Basis);
        Assert.Equal(0.0425m, completion.Charge.CostUsd);
    }

    [Fact]
    public async Task GeminiStreamAssemblesPartsAndReadsUsageMetadata()
    {
        var handler = new StubHttpMessageHandler().EnqueueSse(
        [
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello "}]}}]}""",
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"world"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":900,"candidatesTokenCount":42,"cachedContentTokenCount":100}}""",
        ]);

        var client = new GeminiModelClient(
            new StubHttpClientFactory(handler),
            ModelTestFixtures.Options(BaseUrl),
            ModelTestFixtures.Calculator(),
            new ModelFakeTimeProvider(Now),
            NullLogger<GeminiModelClient>.Instance);

        var events = new List<ModelStreamEvent>();
        await foreach (var @event in client.StreamAsync(
            Request(ModelIdentifier.Parse("google/gemini-2.5-pro")),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.GoogleApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        var completed = Assert.IsType<ModelStreamEvent.Completed>(events[^1]);
        Assert.Equal("Hello world", completed.Completion.Text);
        Assert.Equal(ModelStopReason.EndTurn, completed.Completion.StopReason);
        Assert.Equal(800, completed.Completion.Usage.InputTokens);
        Assert.Equal(100, completed.Completion.Usage.CacheReadInputTokens);
        Assert.Equal(42, completed.Completion.Usage.OutputTokens);
    }

    [Fact]
    public async Task GeminiSendsTheApiKeyAsAHeaderNotAQueryParameter()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""");

        var client = new GeminiModelClient(
            new StubHttpClientFactory(handler),
            ModelTestFixtures.Options(BaseUrl),
            ModelTestFixtures.Calculator(),
            new ModelFakeTimeProvider(Now),
            NullLogger<GeminiModelClient>.Instance);

        await client.CompleteAsync(
            Request(ModelIdentifier.Parse("google/gemini-2.5-flash")),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.GoogleApiKey, "gemini-secret", BaseUrl),
            TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.ToString();
        Assert.DoesNotContain("gemini-secret", uri, StringComparison.Ordinal);
        Assert.Contains("models/gemini-2.5-flash:generateContent", uri, StringComparison.Ordinal);
        Assert.True(handler.Requests[0].Headers.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task AnUnroutableProviderErrorSurfacesAsAModelClientException()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(HttpStatusCode.InternalServerError, "{}");

        var exception = await Assert.ThrowsAsync<ModelClientException>(() => OpenAiClient(handler).CompleteAsync(
            Request(ModelIdentifier.Parse("openai/gpt-5")),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    private static async Task<ModelCompletion> CollectAsync(
        OpenAiCompatibleModelClient client,
        ModelIdentifier model,
        ModelCredentialKind kind = ModelCredentialKind.OpenAiApiKey)
    {
        ModelCompletion? completion = null;
        await foreach (var @event in client.StreamAsync(
            Request(model),
            ModelTestFixtures.ApiKey("k", kind, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken))
        {
            if (@event is ModelStreamEvent.Completed completed)
            {
                completion = completed.Completion;
            }
        }

        Assert.NotNull(completion);
        return completion;
    }

    private static ModelRequest Request(ModelIdentifier model) =>
        ModelRequest.SingleShot(model, "system", "hello");

    private static OpenAiCompatibleModelClient OpenAiClient(StubHttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        ModelTestFixtures.Options(BaseUrl),
        ModelTestFixtures.Calculator(),
        new ModelFakeTimeProvider(Now),
        NullLogger<OpenAiCompatibleModelClient>.Instance);
}
