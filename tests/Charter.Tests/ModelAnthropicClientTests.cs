using System.Net;
using System.Text;
using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The Anthropic client over the official SDK, driven entirely by a stubbed
/// <see cref="HttpMessageHandler"/>. No real network call is made.
/// </summary>
public class ModelAnthropicClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri BaseUrl = new("https://anthropic.test");

    [Fact]
    public async Task ACompletedMessageIsMappedWithItsUsageAndCost()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"text","text":"Refined."}],
             "stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":1000000,"output_tokens":500000,
                      "cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """);

        var completion = await CreateClient(handler).CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), "system", "refine"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken);

        Assert.Equal("Refined.", completion.Text);
        Assert.Equal(ModelStopReason.EndTurn, completion.StopReason);
        Assert.Equal(1_000_000, completion.Usage.InputTokens);
        Assert.Equal(500_000, completion.Usage.OutputTokens);

        // 1M input at $5 plus 0.5M output at $25.
        Assert.Equal(ModelCostBasis.Estimated, completion.Charge.Basis);
        Assert.Equal(17.50m, completion.Charge.CostUsd);
        Assert.Equal(ModelChargeUnit.Usd, completion.Charge.Unit);
    }

    [Fact]
    public async Task AnApiKeyGrantAuthenticatesWithXApiKeyAndASubscriptionWithBearer()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(MinimalMessage)
            .EnqueueJson(MinimalMessage);

        var client = CreateClient(handler);
        var request = ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), null, "hi");

        await client.CompleteAsync(
            request,
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey, "api-key-value", BaseUrl),
            TestContext.Current.CancellationToken);

        await client.CompleteAsync(
            request,
            ModelTestFixtures.ApiKey("s", ModelCredentialKind.AnthropicOAuth, "oauth-token-value", BaseUrl),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "api-key-value",
            handler.Requests[0].Headers.GetValues("x-api-key"),
            StringComparer.Ordinal);
        Assert.Equal("oauth-token-value", handler.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A429IsSurfacedWithTheResetTakenFromTheAnthropicResetHeader()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"rate limit"}}""",
            new Dictionary<string, string>
            {
                ["anthropic-ratelimit-requests-reset"] = "2026-08-10T12:25:00Z",
            });

        var exception = await Assert.ThrowsAsync<ModelRateLimitException>(() => CreateClient(handler).CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), null, "hi"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.Equal(Now.AddMinutes(25), exception.ExhaustedUntil);

        // The SDK is configured with retries off; anything else would burn the reset window.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A401IsAnAuthenticationFailure()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        var exception = await Assert.ThrowsAsync<ModelAuthenticationException>(() =>
            CreateClient(handler).CompleteAsync(
                ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), null, "hi"),
                ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey, baseUrl: BaseUrl),
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task StreamedContentBlockDeltasAssembleIntoOneCompletion()
    {
        var handler = new StubHttpMessageHandler().Enqueue(_ =>
        {
            var body = new StringBuilder();
            Frame(body, "message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":400000,"output_tokens":1}}}""");
            Frame(body, "content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
            Frame(body, "content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The "}}""");
            Frame(body, "content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"refined spec."}}""");
            Frame(body, "content_block_stop", """{"type":"content_block_stop","index":0}""");
            Frame(body, "message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":200000}}""");
            Frame(body, "message_stop", """{"type":"message_stop"}""");

            var content = new StringContent(body.ToString(), Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var events = new List<ModelStreamEvent>();
        await foreach (var @event in CreateClient(handler).StreamAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), null, "refine"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken))
        {
            events.Add(@event);
        }

        Assert.Equal(
            ["The ", "refined spec."],
            events.OfType<ModelStreamEvent.TextDelta>().Select(d => d.Text).ToArray());

        var completed = Assert.IsType<ModelStreamEvent.Completed>(events[^1]);
        Assert.Equal("The refined spec.", completed.Completion.Text);
        Assert.Equal(ModelStopReason.EndTurn, completed.Completion.StopReason);
        Assert.Equal(400_000, completed.Completion.Usage.InputTokens);

        // The final message_delta carries the authoritative output count, not a running total.
        Assert.Equal(200_000, completed.Completion.Usage.OutputTokens);
        Assert.Equal(7.00m, completed.Completion.Charge.CostUsd);
    }

    [Fact]
    public async Task ASubscriptionGrantIsChargedInQuotaEvenThoughTheTokensArePriced()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
             "usage":{"input_tokens":1000000,"output_tokens":0}}
            """);

        var credential = ModelTestFixtures.ApiKey("sub", ModelCredentialKind.AnthropicOAuth, baseUrl: BaseUrl) with
        {
            OwnerUserId = "user-9",
        };

        var completion = await CreateClient(handler).CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("claude-opus-5"), null, "hi"),
            credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelChargeUnit.SubscriptionQuota, completion.Charge.Unit);
        Assert.Equal(0m, completion.Charge.CostUsd);
        Assert.Equal(5.00m, completion.Charge.NotionalCostUsd);
        Assert.Equal("user-9", completion.Charge.OwnerUserId);
    }

    private const string MinimalMessage =
        """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
         "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
         "usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private static void Frame(StringBuilder body, string name, string json) =>
        body.Append("event: ").Append(name).Append('\n')
            .Append("data: ").Append(json).Append("\n\n");

    private static AnthropicModelClient CreateClient(StubHttpMessageHandler handler)
    {
        // The diagnostics handler is what makes the reset header visible to the SDK's exception path;
        // AddCharterModels installs it on the named client, so the test installs it too.
        var pipeline = new ModelHttpDiagnosticsHandler { InnerHandler = handler };

        return new AnthropicModelClient(
            new StubHttpClientFactory(pipeline),
            ModelTestFixtures.Options(BaseUrl),
            ModelTestFixtures.Calculator(),
            new ModelFakeTimeProvider(Now),
            NullLogger<AnthropicModelClient>.Instance);
    }
}
