using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Charter.Diagnostics;

/// <summary>
/// Charter's own <see cref="ActivitySource"/> and <see cref="Meter"/> (section 19.2).
/// </summary>
/// <remarks>
/// Session lifecycle spans - refinement, dispatch, runner execution, PR creation, artifact binding -
/// and the metrics named in section 19.2 hang off these. Everything correlates by session id, which
/// is set both as a log property and as a span attribute so Seq rows and OTLP traces join up.
/// </remarks>
public static class CharterTelemetry
{
    /// <summary>Name registered with the tracer provider.</summary>
    public const string ActivitySourceName = "Charter";

    /// <summary>Name registered with the meter provider.</summary>
    public const string MeterName = "Charter";

    /// <summary>Span attribute every session-scoped activity carries.</summary>
    public const string SessionIdAttribute = "charter.session_id";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, BuildInfo.Version);

    public static Meter Meter { get; } = new(MeterName, BuildInfo.Version);

    /// <summary>Readiness probe outcomes, tagged by result. Cheap, and it catches flapping databases.</summary>
    public static Counter<long> ReadinessChecks { get; } = Meter.CreateCounter<long>(
        "charter.readiness_checks",
        unit: "{check}",
        description: "Readiness probe outcomes, tagged by result.");
}
