using System.Collections.Concurrent;

namespace Charter.Auth.Providers;

/// <summary>
/// Section 31: rate limiting, applied at the one place an unauthenticated stranger can spend the
/// server's time — password verification.
/// </summary>
/// <remarks>
/// Deliberately small and in-process. Charter has one container and Postgres, and no Redis (spec
/// §2.3); a shared counter would be a table write per failed guess. The consequence is that a
/// horizontally scaled deployment throttles per instance, which is documented rather than hidden.
/// </remarks>
public interface ISignInThrottle
{
    /// <summary>False when this key has failed too often recently; <paramref name="retryAfter"/> says how long.</summary>
    bool TryBegin(string key, out TimeSpan retryAfter);

    void RecordFailure(string key);

    /// <summary>Clears the counter after a successful sign-in.</summary>
    void Reset(string key);
}

/// <inheritdoc cref="ISignInThrottle" />
public sealed class SignInThrottle : ISignInThrottle
{
    /// <summary>Generous enough for a person mistyping, tight enough that guessing is pointless.</summary>
    public const int DefaultMaxFailures = 10;

    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock;
    private readonly int maxFailures;
    private readonly TimeSpan window;

    public SignInThrottle(TimeProvider clock, int maxFailures = DefaultMaxFailures, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFailures, 1);

        this.clock = clock;
        this.maxFailures = maxFailures;
        this.window = window ?? TimeSpan.FromMinutes(15);
    }

    /// <inheritdoc />
    public bool TryBegin(string key, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        retryAfter = TimeSpan.Zero;

        if (!windows.TryGetValue(key, out var existing))
        {
            return true;
        }

        var now = clock.GetUtcNow();

        if (now - existing.StartedAt >= window)
        {
            windows.TryRemove(key, out _);
            return true;
        }

        if (existing.Failures < maxFailures)
        {
            return true;
        }

        retryAfter = existing.StartedAt + window - now;
        return false;
    }

    /// <inheritdoc />
    public void RecordFailure(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = clock.GetUtcNow();

        windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) => now - existing.StartedAt >= window
                ? new Window(now, 1)
                : existing with { Failures = existing.Failures + 1 });
    }

    /// <inheritdoc />
    public void Reset(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        windows.TryRemove(key, out _);
    }

    private sealed record Window(DateTimeOffset StartedAt, int Failures);
}
