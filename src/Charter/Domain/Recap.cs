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

    private Recap(
        Guid id,
        Guid sessionId,
        string bodyMd,
        string riskItems,
        string payload,
        decimal costUsd,
        DateTimeOffset generatedAt)
    {
        Id = id;
        SessionId = sessionId;
        BodyMd = bodyMd;
        RiskItems = riskItems;
        Payload = payload;
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

    /// <summary>
    /// jsonb: the structured recap — summary, deviations, what could not be verified, and the
    /// specification in full for an auto-dispatched session (section 7.5).
    /// </summary>
    /// <remarks>
    /// <see cref="BodyMd"/> stays because it is what gets posted as a provider comment. This is the
    /// same content as data, so a reader does not have to parse section headings back out of the
    /// prose to serve it — a coupling that made renaming a heading an undeclared API change.
    /// <c>{}</c> means a row written before the column existed, and reads as absent rather than as
    /// an empty recap.
    /// </remarks>
    public string Payload { get; private set; } = "{}";

    public decimal CostUsd { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    /// <param name="sessionId">The session recapped.</param>
    /// <param name="bodyMd">The markdown body, as it was published.</param>
    /// <param name="riskItems">The risk-ranked file list, as jsonb text.</param>
    /// <param name="costUsd">What the recap pass itself cost.</param>
    /// <param name="now">The clock.</param>
    /// <param name="id">The identifier, where the caller has one.</param>
    /// <param name="payloadJson">
    /// The structured recap. Optional so a caller that only has the prose can still record one; such
    /// a row stores <c>{}</c> and every structured reader treats it as absent.
    /// </param>
    public static Recap Generate(
        Guid sessionId,
        string bodyMd,
        string riskItems,
        decimal costUsd,
        DateTimeOffset? now = null,
        Guid? id = null,
        string? payloadJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMd);
        ArgumentException.ThrowIfNullOrWhiteSpace(riskItems);
        ArgumentOutOfRangeException.ThrowIfNegative(costUsd);

        return new Recap(
            id ?? Guid.CreateVersion7(),
            sessionId,
            bodyMd,
            riskItems,
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            costUsd,
            DomainTime.Resolve(now));
    }
}
