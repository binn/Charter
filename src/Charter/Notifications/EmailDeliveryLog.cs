namespace Charter.Notifications;

/// <summary>One attempted delivery, as the admin settings page shows it.</summary>
public sealed record EmailDeliveryRecord
{
    /// <summary>When the attempt finished.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Who it was for.</summary>
    public required string Recipient { get; init; }

    /// <summary>The template: <c>invitation</c>, <c>needs_input</c>, and so on.</summary>
    public required string Kind { get; init; }

    /// <summary>What happened.</summary>
    public required EmailDeliveryStatus Status { get; init; }

    /// <summary>A sentence for the administrator.</summary>
    public required string Summary { get; init; }

    /// <summary>The mail server's own words, when it gave any.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The record of recent sends that change spec 001 C.3 requires be surfaced in admin settings.
/// </summary>
public interface IEmailDeliveryLog
{
    /// <summary>Records one attempt.</summary>
    void Record(EmailDeliveryRecord record);

    /// <summary>The most recent attempts, newest first.</summary>
    IReadOnlyList<EmailDeliveryRecord> Recent(int limit = 50);

    /// <summary>The most recent failure, or <c>null</c>. What the settings page leads with.</summary>
    EmailDeliveryRecord? LastFailure { get; }
}

/// <summary>
/// A bounded, in-memory ring of recent delivery attempts.
/// </summary>
/// <remarks>
/// <para>
/// The point of change spec 001 C.3 is that a failure is <em>visible</em>. A log line satisfies an
/// operator with a log platform and nobody else, and the self-hoster this project is aimed at will
/// find out that mail is broken when a new hire cannot sign in. So every attempt is also recorded
/// here, where the settings page can read it without a query.
/// </para>
/// <para>
/// Memory rather than a table, for now, and that is a real limitation worth stating: the list is
/// empty after a restart. It is the right trade for this change - a persisted delivery log is a
/// migration and an entity, both owned elsewhere - and the interface is what a database-backed
/// implementation would replace, not the callers.
/// </para>
/// </remarks>
public sealed class RecentEmailDeliveryLog : IEmailDeliveryLog
{
    /// <summary>How many attempts are kept.</summary>
    public const int Capacity = 200;

    private readonly Queue<EmailDeliveryRecord> records = new();
    private readonly Lock gate = new();

    private EmailDeliveryRecord? lastFailure;

    /// <inheritdoc />
    public EmailDeliveryRecord? LastFailure
    {
        get
        {
            lock (gate)
            {
                return lastFailure;
            }
        }
    }

    /// <inheritdoc />
    public void Record(EmailDeliveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (gate)
        {
            records.Enqueue(record);

            while (records.Count > Capacity)
            {
                _ = records.Dequeue();
            }

            if (record.Status is EmailDeliveryStatus.Failed)
            {
                lastFailure = record;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EmailDeliveryRecord> Recent(int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (gate)
        {
            return [.. records.Reverse().Take(limit)];
        }
    }
}
