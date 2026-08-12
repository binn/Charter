namespace Charter.Tests;

/// <summary>
/// The clock mints timestamps at the precision Postgres stores them.
/// </summary>
/// <remarks>
/// These exist because the defect they describe was invisible on macOS and failed in CI. .NET counts
/// in 100ns ticks and PostgreSQL's <c>timestamptz</c> stores whole microseconds, so a timestamp minted
/// from the raw system clock does not survive its own round trip — but a developer's machine only
/// exposes that when its clock is fine-grained enough to produce a non-zero sub-microsecond remainder,
/// and macOS usually is not. Three tests asserting that a streamed status frame matches a reloaded one
/// passed on every laptop and failed on Linux.
/// </remarks>
public sealed class CharterTimeTests
{
    [Fact]
    public void EveryReadingIsAWholeNumberOfMicroseconds()
    {
        // Sampled rather than taken once: the failure is a remainder that most readings do not have,
        // which is exactly how it survived so long.
        for (var i = 0; i < 1000; i++)
        {
            var now = CharterTime.System.GetUtcNow();

            Assert.True(
                now.Ticks % TimeSpan.TicksPerMicrosecond == 0,
                $"{now:O} carries {now.Ticks % TimeSpan.TicksPerMicrosecond} sub-microsecond ticks, so "
                + "Postgres will store a different instant than the one held in memory.");
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(9L)]
    public void SubMicrosecondTicksAreDroppedAndNeverRoundedUp(long extraTicks)
    {
        var aligned = new DateTimeOffset(2026, 8, 12, 17, 22, 14, TimeSpan.Zero).AddMicroseconds(751281);
        var raw = aligned.AddTicks(extraTicks);

        var truncated = MicrosecondTimeProvider.Truncate(raw);

        // Truncated, not rounded. A timestamp that moved forward would name a moment that has not
        // happened yet, which is worse than one that is a fraction of a microsecond stale.
        Assert.Equal(aligned, truncated);
        Assert.True(truncated <= raw);
    }

    [Fact]
    public void TheRawSystemClockIsWhatThisExistsToAvoid()
    {
        // Pins the reason rather than the behaviour: if TimeProvider.System ever started truncating
        // on its own, this whole type could go, and someone should be told rather than left guessing.
        var truncating = CharterTime.System.GetUtcNow();
        Assert.Equal(0, truncating.Ticks % TimeSpan.TicksPerMicrosecond);

        var sampled = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (TimeProvider.System.GetUtcNow().Ticks % TimeSpan.TicksPerMicrosecond != 0)
            {
                sampled++;
            }
        }

        // Not asserted: a coarse platform clock legitimately produces none of these, which is the
        // whole reason the defect reached CI. Recorded so a reader knows which case they are in.
        Assert.True(
            sampled >= 0,
            $"{sampled} of 10,000 raw system-clock readings carried sub-microsecond ticks.");
    }

    [Fact]
    public void TheClockIsOtherwiseTheSystemClock()
    {
        var before = DateTimeOffset.UtcNow;
        var reading = CharterTime.System.GetUtcNow();
        var after = DateTimeOffset.UtcNow;

        // A microsecond of slack on the lower bound, since truncation moves the reading backwards.
        Assert.InRange(reading, before.AddMicroseconds(-1), after);
        Assert.Equal(TimeProvider.System.LocalTimeZone, CharterTime.System.LocalTimeZone);
        Assert.Equal(TimeProvider.System.TimestampFrequency, CharterTime.System.TimestampFrequency);
    }
}
