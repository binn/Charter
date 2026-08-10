namespace Charter.Domain;

/// <summary>
/// The post-session narrative of section 13, generated over the completed session's real events so
/// every explanation is grounded in what actually happened.
/// </summary>
/// <remarks>
/// Generated lazily, only when the user opens the tab, and billed to the teaching budget line
/// (section 34.6) — bundled with build spend it is the first thing an admin cuts, because it has no
/// immediately visible output.
/// </remarks>
public sealed class Walkthrough
{
    private Walkthrough()
    {
    }

    private Walkthrough(
        Guid id,
        Guid sessionId,
        TeachingLevel level,
        string bodyMd,
        decimal costUsd,
        DateTimeOffset generatedAt)
    {
        Id = id;
        SessionId = sessionId;
        Level = level;
        BodyMd = bodyMd;
        CostUsd = costUsd;
        GeneratedAt = generatedAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>
    /// The calibration this rendering was generated at. Held per walkthrough so the always-visible
    /// <em>more detail</em> / <em>less detail</em> override does not change the user's default.
    /// </summary>
    public TeachingLevel Level { get; private set; }

    public string BodyMd { get; private set; } = string.Empty;

    public decimal CostUsd { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public static Walkthrough Generate(
        Guid sessionId,
        TeachingLevel level,
        string bodyMd,
        decimal costUsd,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMd);
        ArgumentOutOfRangeException.ThrowIfNegative(costUsd);

        return new Walkthrough(
            id ?? Guid.CreateVersion7(),
            sessionId,
            level,
            bodyMd,
            costUsd,
            DomainTime.Resolve(now));
    }
}
