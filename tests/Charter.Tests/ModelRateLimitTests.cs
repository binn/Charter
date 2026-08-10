using System.Globalization;
using System.Net;
using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>Section 20b.4: exhaustion, reset extraction, and never failing over mid-session.</summary>
public class ModelRateLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri BaseUrl = new("https://provider.test/v1/");

    [Fact]
    public void RetryAfterInSecondsBecomesAnAbsoluteReset()
    {
        var reset = RateLimitResetParser.Parse(
            new Dictionary<string, string> { ["retry-after"] = "90" },
            Now);

        Assert.Equal(Now.AddSeconds(90), reset.ResetAt);
        Assert.Equal(TimeSpan.FromSeconds(90), reset.RetryAfter);
    }

    [Fact]
    public void RetryAfterAsAnHttpDateIsUnderstood()
    {
        var reset = RateLimitResetParser.Parse(
            new Dictionary<string, string> { ["Retry-After"] = "Mon, 10 Aug 2026 12:05:00 GMT" },
            Now);

        Assert.Equal(Now.AddMinutes(5), reset.ResetAt);
    }

    [Fact]
    public void AnthropicResetHeadersAreRfc3339Timestamps()
    {
        var reset = RateLimitResetParser.Parse(
            new Dictionary<string, string>
            {
                ["anthropic-ratelimit-requests-reset"] = "2026-08-10T12:10:00Z",
                ["anthropic-ratelimit-tokens-reset"] = "2026-08-10T12:03:00Z",
            },
            Now);

        // The earliest advertised reset wins: waiting longer than necessary parks a session.
        Assert.Equal(Now.AddMinutes(3), reset.ResetAt);
    }

    [Fact]
    public void OpenAiResetHeadersUseADurationForm()
    {
        var reset = RateLimitResetParser.Parse(
            new Dictionary<string, string> { ["x-ratelimit-reset-requests"] = "6m0s" },
            Now);

        Assert.Equal(Now.AddMinutes(6), reset.ResetAt);
    }

    [Theory]
    [InlineData("1s", 1000)]
    [InlineData("120ms", 120)]
    [InlineData("6m0s", 360_000)]
    [InlineData("1h30m0s", 5_400_000)]
    public void DurationFormsParse(string value, double expectedMilliseconds)
    {
        Assert.True(RateLimitResetParser.TryParseGoDuration(value, out var duration));
        Assert.Equal(expectedMilliseconds, duration.TotalMilliseconds, 3);
    }

    [Fact]
    public void OpenRouterResetHeaderIsUnixMilliseconds()
    {
        var epoch = Now.AddMinutes(15).ToUnixTimeMilliseconds();

        var reset = RateLimitResetParser.Parse(
            new Dictionary<string, string>
            {
                ["x-ratelimit-reset"] = epoch.ToString(CultureInfo.InvariantCulture),
            },
            Now);

        Assert.Equal(Now.AddMinutes(15), reset.ResetAt);
    }

    [Fact]
    public void NoResetHeadersMeansUnknownRatherThanZero()
    {
        var reset = RateLimitResetParser.Parse(new Dictionary<string, string>(), Now);

        Assert.False(reset.IsKnown);
        Assert.Null(reset.ResetAt);
    }

    [Fact]
    public async Task A429FromAnOpenAiCompatibleProviderRaisesARateLimitWithItsReset()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"rate limit exceeded"}}""",
            new Dictionary<string, string> { ["retry-after"] = "42" });

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ModelRateLimitException>(() => client.CompleteAsync(
            Request(),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.Equal(Now.AddSeconds(42), exception.ExhaustedUntil);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A429IsNotBlindRetried()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.TooManyRequests,
            "{}",
            new Dictionary<string, string> { ["retry-after"] = "5" });

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ModelRateLimitException>(() => client.CompleteAsync(
            Request(),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        // Exactly one attempt: retrying would spend the reset window and hide the exhaustion.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A401IsAnAuthenticationFailureNotARateLimit()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"invalid api key"}}""");

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ModelAuthenticationException>(() => client.CompleteAsync(
            Request(),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task A403WithoutResetInformationIsAnAuthenticationFailure()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(HttpStatusCode.Forbidden, "{}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ModelAuthenticationException>(() => client.CompleteAsync(
            Request(),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A403CarryingAResetIsTreatedAsQuotaExhaustion()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Forbidden,
            "{}",
            new Dictionary<string, string> { ["retry-after"] = "600" });

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ModelRateLimitException>(() => client.CompleteAsync(
            Request(),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, baseUrl: BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.Equal(Now.AddMinutes(10), exception.ExhaustedUntil);
    }

    [Fact]
    public void MidSessionExhaustionUnderThePauseAndResumeDefaultNeverSwitchesModel()
    {
        var next = ModelCredentialResolution.Success(new ResolvedModelCredential(
            ModelTestFixtures.ApiKey("other", ModelCredentialKind.OpenAiApiKey),
            ModelCredentialTier.OrganizationMeteredKey));

        var decision = ModelFailoverPlanner.Decide(
            ModelFailoverPolicy.PauseAndResume,
            sessionInProgress: true,
            next,
            Now.AddHours(1));

        Assert.Equal(ModelFailoverAction.PauseUntilReset, decision.Action);
        Assert.Null(decision.NextCredential);
        Assert.Equal(Now.AddHours(1), decision.ResumeAt);
    }

    [Fact]
    public void MidSessionExhaustionUnderRestartStepRedoesTheStepOnTheNextCredential()
    {
        var next = ModelCredentialResolution.Success(new ResolvedModelCredential(
            ModelTestFixtures.ApiKey("other", ModelCredentialKind.OpenAiApiKey),
            ModelCredentialTier.OrganizationMeteredKey));

        var decision = ModelFailoverPlanner.Decide(
            ModelFailoverPolicy.RestartStep,
            sessionInProgress: true,
            next,
            Now.AddHours(1));

        Assert.Equal(ModelFailoverAction.RestartStepUnderNextCredential, decision.Action);
        Assert.Equal("other", decision.NextCredential!.Credential.Id);
    }

    [Fact]
    public void RestartStepWithNothingToRestartUnderFallsBackToWaiting()
    {
        var decision = ModelFailoverPlanner.Decide(
            ModelFailoverPolicy.RestartStep,
            sessionInProgress: true,
            ModelCredentialResolution.Exhausted(Now.AddHours(4), anyCandidates: true),
            Now.AddHours(2));

        Assert.Equal(ModelFailoverAction.PauseUntilReset, decision.Action);
        Assert.Equal(Now.AddHours(2), decision.ResumeAt);
    }

    [Fact]
    public void BetweenSessionsFailoverIsFreeAndSilent()
    {
        var next = ModelCredentialResolution.Success(new ResolvedModelCredential(
            ModelTestFixtures.ApiKey("other", ModelCredentialKind.OpenAiApiKey),
            ModelCredentialTier.OpenRouter));

        var decision = ModelFailoverPlanner.Decide(
            ModelFailoverPolicy.PauseAndResume,
            sessionInProgress: false,
            next,
            Now.AddHours(1));

        Assert.Equal(ModelFailoverAction.UseNextCredential, decision.Action);
        Assert.Equal("other", decision.NextCredential!.Credential.Id);
    }

    [Fact]
    public void EverythingExhaustedBetweenSessionsQueuesRatherThanFails()
    {
        var decision = ModelFailoverPlanner.Decide(
            ModelFailoverPolicy.PauseAndResume,
            sessionInProgress: false,
            ModelCredentialResolution.Exhausted(Now.AddMinutes(20), anyCandidates: true),
            null);

        Assert.Equal(ModelFailoverAction.PauseUntilReset, decision.Action);
        Assert.Equal(Now.AddMinutes(20), decision.ResumeAt);
    }

    private static ModelRequest Request() => ModelRequest.SingleShot(
        ModelIdentifier.Parse("openai/gpt-5"),
        "system",
        "hello");

    private static OpenAiCompatibleModelClient CreateClient(StubHttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        ModelTestFixtures.Options(BaseUrl),
        ModelTestFixtures.Calculator(),
        new ModelFakeTimeProvider(Now),
        NullLogger<OpenAiCompatibleModelClient>.Instance);
}
