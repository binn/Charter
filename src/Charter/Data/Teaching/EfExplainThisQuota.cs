using Charter.Domain;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Charter.Data.Teaching;

/// <summary>
/// Section 13's per-user daily cap on <em>explain this</em>, counted in Postgres.
/// </summary>
/// <remarks>
/// <para>
/// A counter table (<c>explain_this_usage</c>, one row per user per UTC day) rather than a count
/// over <c>LedgerEntry where category = teach</c>. The reasoning is on
/// <see cref="ExplainThisUsage"/>: the <c>teach</c> category covers all three teaching surfaces, so
/// counting it would spend the explain-this allowance on a walkthrough; ledger entries move through
/// reserved → settled → released (section 34.4), so the count at check time is not a decision; and
/// the cap has to be consumed <em>before</em> the model call, when no ledger entry exists yet.
/// </para>
/// <para>
/// One statement, <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>. A read-then-write would let a
/// reader who clicks ten times in a second spend ten allowances against the same count; the upsert
/// serialises them on the row and returns the count each caller actually took.
/// </para>
/// </remarks>
public sealed class EfExplainThisQuota : IExplainThisQuota
{
    /// <summary>
    /// How long a spent day is kept. Long enough to answer "why did I run out yesterday", short
    /// enough that the table stays one row per active user.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Take one, atomically, and say what the count now is.</summary>
    internal const string ConsumeSql = """
        INSERT INTO explain_this_usage (user_id, day, used, last_used_at)
        VALUES (@user_id, @day, 1, @now)
        ON CONFLICT (user_id, day) DO UPDATE
        SET used = explain_this_usage.used + 1,
            last_used_at = EXCLUDED.last_used_at
        RETURNING used
        """;

    /// <summary>
    /// Drop this reader's spent days. Scoped to the one user and driven by the primary key, so it
    /// costs nothing next to the upsert it rides along with.
    /// </summary>
    internal const string PruneSql = """
        DELETE FROM explain_this_usage
        WHERE user_id = @user_id AND day < @cutoff
        """;

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    /// <summary>Creates the quota.</summary>
    public EfExplainThisQuota(IServiceScopeFactory scopes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ExplainThisAllowance> TryConsumeAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var day = ExplainThisUsage.DayOf(now);
        var resets = ExplainThisUsage.ResetAfter(day);

        if (limit <= 0)
        {
            // A cap of zero is "teaching is off for this person". Writing a row would record a spend
            // that never happened and would still be there when somebody turns it back on.
            return new ExplainThisAllowance(false, 0, limit, resets);
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        await using var consume = await RawSqlCommand
            .CreateAsync(db, ConsumeSql, cancellationToken)
            .ConfigureAwait(false);

        consume.AddParameter("user_id", NpgsqlDbType.Uuid, userId);
        consume.AddParameter("day", NpgsqlDbType.Date, day);
        consume.AddParameter("now", NpgsqlDbType.TimestampTz, now);

        var used = (int)(await consume.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await using var prune = await RawSqlCommand
            .CreateAsync(db, PruneSql, cancellationToken)
            .ConfigureAwait(false);

        prune.AddParameter("user_id", NpgsqlDbType.Uuid, userId);
        prune.AddParameter("cutoff", NpgsqlDbType.Date, day.AddDays(-(int)Retention.TotalDays));

        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Reported capped, so the UI says "20 of 20" rather than "37 of 20" to somebody who kept
        // clicking after the refusal.
        return new ExplainThisAllowance(used <= limit, Math.Min(used, limit), limit, resets);
    }

    /// <summary>How many explanations <paramref name="userId"/> has spent today. For the UI.</summary>
    public async Task<int> UsedTodayAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var day = ExplainThisUsage.DayOf(_clock.GetUtcNow());

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        return await db.ExplainThisUsage
            .AsNoTracking()
            .Where(usage => usage.UserId == userId && usage.Day == day)
            .Select(usage => usage.Used)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
