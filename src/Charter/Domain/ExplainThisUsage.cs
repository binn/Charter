namespace Charter.Domain;

/// <summary>
/// One person's <em>explain this</em> spend for one UTC day (section 13).
/// </summary>
/// <remarks>
/// <para>
/// Section 13 singles this surface out: inline annotations and the walkthrough are bounded by the
/// session — one milestone list, one narrative — while <em>explain this</em> is a button next to
/// every event, file and hunk, and is therefore "unbounded, so this is the one that needs a per-user
/// cap".
/// </para>
/// <para>
/// A counter row rather than a count over <c>LedgerEntry where category = teach</c>, for three
/// reasons. The ledger's <c>teach</c> category covers all three surfaces, so counting it would spend
/// the explain-this allowance on a walkthrough somebody opened once. Ledger entries move through
/// <c>reserved → settled → released</c> (section 34.4), so the count at the moment of the check is
/// not a decision, it is a guess. And the cap has to be consumed <em>before</em> the model call,
/// where a ledger entry does not exist yet — so a row per user per day, incremented by one atomic
/// upsert, is both cheaper and correct under a burst of clicks.
/// </para>
/// <para>
/// The window is the UTC day. Not the user's local day: the alternative is a timezone column on
/// every user and a reset that lands at a different instant for each of them, for a cap whose whole
/// job is to stop a runaway afternoon.
/// </para>
/// </remarks>
public sealed class ExplainThisUsage
{
    private ExplainThisUsage()
    {
    }

    private ExplainThisUsage(Guid userId, DateOnly day, int used, DateTimeOffset lastUsedAt)
    {
        UserId = userId;
        Day = day;
        Used = used;
        LastUsedAt = lastUsedAt;
    }

    /// <summary>Half of the primary key. There is no surrogate id: the pair is the identity.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The UTC day this counts. A <c>date</c> column, never a timestamp.</summary>
    public DateOnly Day { get; private set; }

    /// <summary>How many explanations were asked for on that day.</summary>
    public int Used { get; private set; }

    public DateTimeOffset LastUsedAt { get; private set; }

    /// <summary>The UTC day <paramref name="now"/> falls in.</summary>
    public static DateOnly DayOf(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    /// <summary>Midnight UTC after <paramref name="day"/> — when the allowance rolls over.</summary>
    public static DateTimeOffset ResetAfter(DateOnly day)
        => new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static ExplainThisUsage Start(Guid userId, DateTimeOffset now)
        => new(userId, DayOf(now), 1, DomainTime.Resolve(now));

    public void Consume(DateTimeOffset now)
    {
        Used++;
        LastUsedAt = DomainTime.Resolve(now);
    }
}
