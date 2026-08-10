using System.Globalization;

namespace Charter.Agent.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Where the daemon writes. Abstracted so tests can assert on what was written.</summary>
public interface IAgentLog
{
    void Write(LogLevel level, string message);
}

public static class AgentLogExtensions
{
    public static void Debug(this IAgentLog log, string message) => Write(log, LogLevel.Debug, message);

    public static void Info(this IAgentLog log, string message) => Write(log, LogLevel.Info, message);

    public static void Warn(this IAgentLog log, string message) => Write(log, LogLevel.Warning, message);

    public static void Error(this IAgentLog log, string message) => Write(log, LogLevel.Error, message);

    private static void Write(IAgentLog log, LogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(level, message);
    }
}

/// <summary>
/// The daemon's log: one line per event, scrubbed on the way out.
/// </summary>
/// <remarks>
/// Every line passes through <see cref="SecretScrubber"/> before it is written, so a value that
/// reaches the logger by accident — most plausibly in a child process's own output — still does not
/// reach the operator's terminal or their journal.
/// </remarks>
public sealed class ConsoleAgentLog(SecretScrubber scrubber, LogLevel minimum = LogLevel.Info, TextWriter? writer = null)
    : IAgentLog
{
    private readonly SecretScrubber _scrubber = scrubber;
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly Lock _gate = new();

    public LogLevel Minimum { get; } = minimum;

    public void Write(LogLevel level, string message)
    {
        if (level < Minimum)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Label(level)} {_scrubber.Scrub(message)}");

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "dbg",
        LogLevel.Info => "inf",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}
