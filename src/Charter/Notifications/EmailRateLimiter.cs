namespace Charter.Notifications;

/// <summary>Whether one more message may go to a recipient right now.</summary>
public interface IEmailRateLimiter
{
    /// <summary>
    /// Takes a slot for <paramref name="recipient"/> in <paramref name="category"/>, or reports how
    /// long until one frees up.
    /// </summary>
    bool TryAcquire(string recipient, EmailCategory category, out TimeSpan retryAfter);

    /// <summary>How many slots remain in this recipient's bucket, for the settings page.</summary>
    int Remaining(string recipient, EmailCategory category);
}

/// <summary>
/// A sliding one-hour window per recipient and category (change spec 001, part C.3).
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is specific: a repo that flaps between <c>Running</c> and
/// <c>NeedsInput</c>, or a webhook replayed a hundred times, turning into a hundred emails to one
/// person. Section 6 already narrows what may notify to two states, and this is the second half of
/// the same argument - Charter gets muted in a week either way, whether by the number of states that
/// notify or the number of times one of them fires.
/// </para>
/// <para>
/// In memory, and deliberately so. The hard constraint against in-memory state is about
/// orchestration - a session must be resumable from Postgres after a restart - and a rate limiter is
/// the opposite case: after a restart the correct behaviour is to allow mail again, because the
/// storm that justified holding it back is over. Section 7.2a settles the other half, since one
/// instance serves one organisation and there is no second process to coordinate with.
/// </para>
/// </remarks>
public sealed class EmailRateLimiter : IEmailRateLimiter
{
    /// <summary>The window every count is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly Dictionary<(string Recipient, EmailCategory Category), List<DateTimeOffset>> sends = new();
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly int limit;

    /// <summary>Creates a limiter allowing <paramref name="limit"/> messages per bucket per hour.</summary>
    public EmailRateLimiter(int limit, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentNullException.ThrowIfNull(clock);

        this.limit = limit;
        this.clock = clock;
    }

    /// <summary>The per-bucket ceiling.</summary>
    public int Limit => limit;

    /// <inheritdoc />
    public bool TryAcquire(string recipient, EmailCategory category, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        var now = clock.GetUtcNow();
        retryAfter = TimeSpan.Zero;

        lock (gate)
        {
            var window = Prune(recipient, category, now);

            if (window.Count >= limit)
            {
                // The oldest send is what has to age out before there is room.
                retryAfter = window[0] + Window - now;
                if (retryAfter < TimeSpan.Zero)
                {
                    retryAfter = TimeSpan.Zero;
                }

                return false;
            }

            window.Add(now);
            return true;
        }
    }

    /// <inheritdoc />
    public int Remaining(string recipient, EmailCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        lock (gate)
        {
            return Math.Max(0, limit - Prune(recipient, category, clock.GetUtcNow()).Count);
        }
    }

    private List<DateTimeOffset> Prune(string recipient, EmailCategory category, DateTimeOffset now)
    {
        var key = (Key(recipient), category);

        if (!sends.TryGetValue(key, out var window))
        {
            window = [];
            sends[key] = window;
        }

        window.RemoveAll(sent => now - sent >= Window);
        Compact(key, now);

        return window;
    }

    /// <summary>
    /// Drops buckets nothing has been sent to within the window.
    /// </summary>
    /// <remarks>
    /// Buckets are keyed by address, and an instance with a long uptime would otherwise accumulate
    /// one per address it has ever mailed. The bucket being pruned is never dropped, because the
    /// caller is holding the list and is about to add to it.
    /// </remarks>
    private void Compact((string Recipient, EmailCategory Category) keep, DateTimeOffset now)
    {
        const int compactAbove = 64;

        if (sends.Count <= compactAbove)
        {
            return;
        }

        var stale = sends
            .Where(entry => entry.Key != keep &&
                            (entry.Value.Count == 0 || now - entry.Value[^1] >= Window))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in stale)
        {
            sends.Remove(key);
        }
    }

    /// <summary>
    /// Addresses are compared case-insensitively, so a limit cannot be walked around by capitalising
    /// the local part.
    /// </summary>
    private static string Key(string recipient) => recipient.Trim().ToLowerInvariant();
}
