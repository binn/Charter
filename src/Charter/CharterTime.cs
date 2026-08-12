namespace Charter;

/// <summary>
/// The clock Charter mints every timestamp from.
/// </summary>
/// <remarks>
/// <para>
/// Identical to <see cref="TimeProvider.System"/> except that it truncates to whole microseconds,
/// which is the precision PostgreSQL's <c>timestamptz</c> stores. .NET counts in 100ns ticks, so a
/// timestamp minted from the system clock and written to Postgres comes back a few ticks earlier
/// than it went in — and the in-memory entity keeps the value that no longer matches its own row.
/// </para>
/// <para>
/// That mattered because section 11 makes the rows the record and the live stream a courtesy on top
/// of them: a status frame pushed to an open thread carried <c>2026-08-12T17:22:14.7512816Z</c> while
/// a reload of the same event returned <c>…14.751281Z</c>. Two spellings of one instant, differing by
/// less than a microsecond, is still two spellings — and anything downstream comparing or keying on
/// the value sees two events.
/// </para>
/// <para>
/// Truncating where the timestamp is minted makes the round trip lossless by construction, rather
/// than asking every comparison to know about database precision. Note it truncates rather than
/// rounds: a timestamp must never move forward into a moment that has not happened.
/// </para>
/// <para>
/// This is also why it is <c>CharterTime.System</c> rather than <c>TimeProvider.System</c>
/// everywhere. It lives in the root namespace so that every nested namespace — including
/// <c>Charter.Tests</c> — resolves it without a using directive, and so that reaching for the raw
/// system clock is the conspicuous choice.
/// </para>
/// </remarks>
public sealed class MicrosecondTimeProvider : TimeProvider
{
    private readonly TimeProvider inner;

    /// <summary>Wraps <paramref name="inner"/>, truncating its readings to whole microseconds.</summary>
    public MicrosecondTimeProvider(TimeProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <summary>Truncates <paramref name="value"/> down to a whole microsecond.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value)
        => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Truncate(inner.GetUtcNow());

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    /// <inheritdoc />
    public override long TimestampFrequency => inner.TimestampFrequency;

    /// <inheritdoc />
    public override long GetTimestamp() => inner.GetTimestamp();

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
        => inner.CreateTimer(callback, state, dueTime, period);
}

/// <summary>Charter's clock.</summary>
public static class CharterTime
{
    /// <summary>
    /// The system clock, truncated to the precision Postgres stores. Use this rather than
    /// <see cref="TimeProvider.System"/>; see <see cref="MicrosecondTimeProvider"/> for why.
    /// </summary>
    public static readonly TimeProvider System = new MicrosecondTimeProvider(TimeProvider.System);
}
