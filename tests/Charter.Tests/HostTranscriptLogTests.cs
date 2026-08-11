using Charter.Configuration;
using Charter.Hosting;
using Charter.Logging;
using Charter.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c> decides whether repository content reaches the operator's
/// log platform (section 19).
/// </summary>
/// <remarks>
/// <para>
/// The variable parsed, validated, reached <c>StartupOptions</c>, and stopped. The safe default held
/// only by accident - nothing logged a transcript body anywhere - which also meant an operator who
/// turned it on to debug a session got nothing for it. Both halves are tested here: metadata is
/// logged either way, and the body appears only when the flag is set.
/// </para>
/// <para>
/// Section 19 is explicit that the leak warning applies to every sink equally, which is why the
/// decision is made once, in <see cref="TranscriptLog"/>, rather than per sink.
/// </para>
/// </remarks>
public class HostTranscriptLogTests
{
    private static readonly TranscriptEvent Sample = new()
    {
        Type = "model_call",
        CorrelationId = "session-7",
        Model = "openrouter/anthropic/claude-sonnet-5",
        Duration = TimeSpan.FromMilliseconds(1234),
        InputTokens = 900,
        OutputTokens = 120,
        CostUsd = 0.0123m,
        Paths = ["src/Charter/Program.cs"],
        Body = "system: you refine specifications\nuser: the invoice total is wrong on the PDF",
    };

    [Fact]
    public void MetadataIsLoggedWithoutTheFlagAndTheBodyIsNot()
    {
        var sink = new CapturingLoggerProvider();
        var log = new TranscriptLog(Logger(sink), Options(includeTranscripts: false));

        Assert.False(log.BodiesIncluded);
        log.Record(Sample);

        var line = Assert.Single(sink.Lines);

        // Section 19: type, timing, file paths and cost are metadata and are logged by default.
        Assert.Contains("model_call", line, StringComparison.Ordinal);
        Assert.Contains("session-7", line, StringComparison.Ordinal);
        Assert.Contains("1234", line, StringComparison.Ordinal);
        Assert.Contains("0.0123", line, StringComparison.Ordinal);
        Assert.Contains("src/Charter/Program.cs", line, StringComparison.Ordinal);

        // And the requester's business context is not.
        Assert.DoesNotContain("invoice total", line, StringComparison.Ordinal);
        Assert.DoesNotContain("refine specifications", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlagIsWhatPutsTheBodyInTheLog()
    {
        var sink = new CapturingLoggerProvider();
        var log = new TranscriptLog(Logger(sink), Options(includeTranscripts: true));

        Assert.True(log.BodiesIncluded);
        log.Record(Sample);

        var line = Assert.Single(sink.Lines);

        Assert.Contains("model_call", line, StringComparison.Ordinal);
        Assert.Contains("invoice total", line, StringComparison.Ordinal);
    }

    /// <summary>An event with no body is a metadata line either way, not an empty transcript.</summary>
    [Fact]
    public void AnEventWithNoBodyLogsMetadataEvenWhenBodiesAreOn()
    {
        var sink = new CapturingLoggerProvider();
        var log = new TranscriptLog(Logger(sink), Options(includeTranscripts: true));

        log.Record(new TranscriptEvent { Type = "model_call", Outcome = "rate_limited" });

        Assert.Contains("rate_limited", Assert.Single(sink.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// The host registers the real transcript log, and <c>AddCharterModels</c>' <c>TryAdd</c> fallback
    /// does not displace it.
    /// </summary>
    [Fact]
    public void TheHostsTranscriptLogWinsOverTheFallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options(includeTranscripts: true));
        services.AddSingleton<ITranscriptLog, TranscriptLog>();
        services.AddCharterModels();

        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<ITranscriptLog>().BodiesIncluded);
    }

    /// <summary>
    /// A graph that never registered one withholds bodies rather than exporting them.
    /// </summary>
    /// <remarks>
    /// The fallback exists because the alternative - resolving <c>StartupOptions</c> from a graph
    /// that has none - is a startup crash in a subsystem test. Section 19 decides which way it should
    /// fail: towards withholding.
    /// </remarks>
    [Fact]
    public void TheFallbackWithholdsBodies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterModels();

        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<ITranscriptLog>().BodiesIncluded);
    }

    /// <summary>
    /// A model call is described as section 19 asks: facts in the metadata, content in the body.
    /// </summary>
    [Fact]
    public void AModelCallSeparatesItsFactsFromItsContent()
    {
        var request = new ModelRequest
        {
            Model = Charter.Models.ModelIdentifier.Parse("anthropic/claude-opus-5"),
            SystemPrompt = "you refine specifications",
            Messages = [ModelMessage.User("the invoice total is wrong on the PDF")],
            CorrelationId = "session-7",
        };

        var described = TranscriptLoggingModelClient.Describe(
            TranscriptLoggingModelClient.CompletionEventType,
            request,
            completion: null,
            TimeSpan.FromSeconds(2),
            outcome: "rate_limited");

        Assert.Equal("session-7", described.CorrelationId);
        Assert.Equal("rate_limited", described.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), described.Duration);
        Assert.Contains("anthropic", described.Model, StringComparison.Ordinal);

        // The prompt is content, and lives where the flag can withhold it.
        Assert.Contains("invoice total", described.Body!, StringComparison.Ordinal);
    }

    private static ILogger<TranscriptLog> Logger(CapturingLoggerProvider sink)
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(sink);
        });

        return factory.CreateLogger<TranscriptLog>();
    }

    private static StartupOptions Options(bool includeTranscripts)
        => ConfigTestEnvironment
            .Valid(("CHARTER_LOG_INCLUDE_TRANSCRIPTS", includeTranscripts ? "true" : "false"))
            .ToStartupOptions();

    /// <summary>Collects rendered log messages so a test can ask what actually left the process.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Add(formatter(state, exception));
            }
        }
    }
}
