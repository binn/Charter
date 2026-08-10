using System.Net;
using Charter.Models;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// Section 20b.2: never log a token, never return one to the UI. These tests assert the negative -
/// the secret does not appear anywhere it could be written down.
/// </summary>
public class ModelSecretRedactionTests
{
    private const string Secret = "sk-ant-super-secret-value-0123456789";

    /// <summary>An error body of the shape several providers return, echoing the key back.</summary>
    private const string EchoBody =
        "{\"error\":{\"message\":\"Incorrect API key provided: " + Secret + "\"}}";

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri BaseUrl = new("https://provider.test/v1/");

    [Fact]
    public void SecretToStringIsTheRedactionPlaceholder()
    {
        var secret = new ModelSecret(Secret);

        Assert.Equal(ModelSecret.RedactedPlaceholder, secret.ToString());
        Assert.DoesNotContain(Secret, $"{secret}", StringComparison.Ordinal);
        Assert.Equal(Secret, secret.Reveal());
    }

    [Fact]
    public void CredentialToStringNeverPrintsTheSecret()
    {
        var credential = ModelTestFixtures.ApiKey("grant-1", ModelCredentialKind.AnthropicApiKey, Secret);

        var rendered = credential.ToString();

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("grant-1", rendered, StringComparison.Ordinal);
        Assert.Contains(ModelSecret.RedactedPlaceholder, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void InterpolatingACredentialIntoALogMessageDoesNotLeakIt()
    {
        var credential = ModelTestFixtures.ApiKey("grant-1", ModelCredentialKind.AnthropicApiKey, Secret);

        var line = $"resolved {credential} for the session";

        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactStripsSecretsFromArbitraryText()
    {
        var text = $"upstream said: invalid key {Secret} (request abc)";

        var redacted = ModelSecret.Redact(text, new ModelSecret(Secret));

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.Contains("request abc", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderErrorThatEchoesTheKeyDoesNotPutItInTheExceptionMessage()
    {
        // Several providers echo the offending credential back in their error payload.
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Unauthorized,
            EchoBody);

        var client = CreateClient(handler, out _);

        var exception = await Assert.ThrowsAsync<ModelAuthenticationException>(() => client.CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("openai/gpt-5"), null, "hello"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, Secret, BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingWrittenToTheLoggerContainsTheSecretEvenAtDebug()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Unauthorized,
            EchoBody);

        var client = CreateClient(handler, out var captured);

        await Assert.ThrowsAsync<ModelAuthenticationException>(() => client.CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("openai/gpt-5"), null, "hello"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, Secret, BaseUrl),
            TestContext.Current.CancellationToken));

        Assert.NotEmpty(captured.Messages);
        Assert.All(
            captured.Messages,
            message => Assert.DoesNotContain(Secret, message, StringComparison.Ordinal));
        Assert.Contains(
            captured.Messages,
            message => message.Contains(ModelSecret.RedactedPlaceholder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSecretTravelsInAHeaderAndNeverInTheUrlOrBody()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");

        var client = CreateClient(handler, out _);

        await client.CompleteAsync(
            ModelRequest.SingleShot(ModelIdentifier.Parse("openai/gpt-5"), null, "hello"),
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.OpenAiApiKey, Secret, BaseUrl),
            TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal(Secret, request.Headers.Authorization!.Parameter);
        Assert.DoesNotContain(Secret, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, handler.RequestBodies[0], StringComparison.Ordinal);
    }

    private static OpenAiCompatibleModelClient CreateClient(
        StubHttpMessageHandler handler,
        out CapturingLoggerProvider captured)
    {
        var provider = new CapturingLoggerProvider();
        captured = provider;

        // Not disposed: the returned client keeps using the logger this factory produced.
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });

        return new OpenAiCompatibleModelClient(
            new StubHttpClientFactory(handler),
            ModelTestFixtures.Options(BaseUrl),
            ModelTestFixtures.Calculator(),
            new ModelFakeTimeProvider(Now),
            factory.CreateLogger<OpenAiCompatibleModelClient>());
    }
}
