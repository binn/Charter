namespace Charter.Domain;

/// <summary>
/// The engineer recap of section 14: same event stream as the walkthrough, opposite audience.
/// </summary>
/// <remarks>
/// It must never say "looks good". It is an orientation aid, not a verdict — the moment it
/// editorialises on quality, reviewers start trusting it instead of reading. It is posted as a pull
/// request comment as well as rendered in Charter, because engineers review on GitHub.
/// </remarks>
public sealed class Recap
{
    private Recap()
    {
    }

    private Recap(Guid id, Guid sessionId, string bodyMd, string riskItems, decimal costUsd, DateTimeOffset generatedAt)
    {
        Id = id;
        SessionId = sessionId;
        BodyMd = bodyMd;
        RiskItems = riskItems;
        CostUsd = costUsd;
        GeneratedAt = generatedAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public string BodyMd { get; private set; } = string.Empty;

    /// <summary>
    /// jsonb: the risk-ranked file list, not an alphabetical one. Auth, migrations, money maths,
    /// external calls, dependency changes and denylist-adjacent paths float to the top.
    /// </summary>
    public string RiskItems { get; private set; } = "[]";

    public decimal CostUsd { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public static Recap Generate(
        Guid sessionId,
        string bodyMd,
        string riskItems,
        decimal costUsd,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMd);
        ArgumentException.ThrowIfNullOrWhiteSpace(riskItems);
        ArgumentOutOfRangeException.ThrowIfNegative(costUsd);

        return new Recap(id ?? Guid.CreateVersion7(), sessionId, bodyMd, riskItems, costUsd, DomainTime.Resolve(now));
    }
}
