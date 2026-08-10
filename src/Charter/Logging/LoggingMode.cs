namespace Charter.Logging;

/// <summary>
/// Console sink formatting, selected by the <c>LOGGING_MODE</c> environment variable (section 19.1).
/// </summary>
/// <remarks>
/// The console sink is the only one that always exists, so its shape has to suit wherever Charter is
/// running. An unrecognised value fails startup validation rather than falling back silently.
/// </remarks>
public enum LoggingMode
{
    /// <summary>Serilog's human-readable console template, coloured when the terminal supports it.</summary>
    Default,

    /// <summary>One compact JSON object per line, for Loki, Vector, Fluent Bit, CloudWatch.</summary>
    Json,

    /// <summary>One JSON object per line shaped for Railway's structured log parser.</summary>
    RailwayJson,
}
