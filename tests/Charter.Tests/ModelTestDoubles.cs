using System.Net;
using System.Text;
using Charter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a queue of canned responses and records
/// what it was asked. No test in this suite makes a real network call.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public StubHttpMessageHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public StubHttpMessageHandler EnqueueSse(IEnumerable<string> frames)
        => Enqueue(_ =>
        {
            var builder = new StringBuilder();
            foreach (var frame in frames)
            {
                builder.Append("data: ").Append(frame).Append("\n\n");
            }

            var content = new StringContent(builder.ToString(), Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    public StubHttpMessageHandler EnqueueError(
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
        => Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(
            request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The stub handler ran out of canned responses.");
        }

        return _responses.Dequeue()(request);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over a stub handler.</summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
}

/// <summary>A <see cref="TimeProvider"/> pinned to an instant the test controls.</summary>
public sealed class ModelFakeTimeProvider : TimeProvider
{
    public ModelFakeTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Records what the resolver wrote back, so the section 20b.4 rules can be asserted.</summary>
internal sealed class RecordingCredentialStore : IModelCredentialStore
{
    private readonly IReadOnlyList<ModelCredential> _candidates;

    public RecordingCredentialStore(params ModelCredential[] candidates) => _candidates = candidates;

    public List<(string CredentialId, DateTimeOffset? ExhaustedUntil, bool UseOverflow)> Exhausted { get; } = [];

    public List<(string CredentialId, string Reason)> Invalidated { get; } = [];

    public List<(string CredentialId, ModelUsage Usage, ModelCharge Charge)> Usages { get; } = [];

    public Task<IReadOnlyList<ModelCredential>> GetCandidatesAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default) => Task.FromResult(_candidates);

    public Task MarkExhaustedAsync(
        string credentialId,
        DateTimeOffset? exhaustedUntil,
        bool useOverflow,
        CancellationToken cancellationToken = default)
    {
        Exhausted.Add((credentialId, exhaustedUntil, useOverflow));
        return Task.CompletedTask;
    }

    public Task MarkInvalidAsync(
        string credentialId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Invalidated.Add((credentialId, reason));
        return Task.CompletedTask;
    }

    public Task RecordUsageAsync(
        string credentialId,
        ModelUsage usage,
        ModelCharge charge,
        CancellationToken cancellationToken = default)
    {
        Usages.Add((credentialId, usage, charge));
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="ILogger"/> that keeps every formatted message for inspection.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages;

        public CapturingLogger(List<string> messages) => _messages = messages;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                _messages.Add(exception.ToString());
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}

/// <summary>Shared fixtures for the model tests.</summary>
internal static class ModelTestFixtures
{
    public static ModelCredential ApiKey(
        string id,
        ModelCredentialKind kind,
        string secret = "sk-test-not-a-real-key",
        Uri? baseUrl = null) => new()
        {
            Id = id,
            Kind = kind,
            Secret = new ModelSecret(secret),
            BaseUrl = baseUrl,
        };

    public static ILogger<T> Silent<T>() => NullLogger<T>.Instance;

    public static ModelClientOptions Options(Uri baseUrl) => new()
    {
        DefaultBaseUrls = new Dictionary<ModelProvider, Uri>
        {
            [ModelProvider.OpenAi] = baseUrl,
            [ModelProvider.OpenRouter] = baseUrl,
            [ModelProvider.Google] = baseUrl,
            [ModelProvider.Anthropic] = baseUrl,
            [ModelProvider.OpenAiCompatible] = baseUrl,
        },
    };

    public static IModelCostCalculator Calculator() =>
        new ModelCostCalculator(new StaticModelPriceTable());
}
