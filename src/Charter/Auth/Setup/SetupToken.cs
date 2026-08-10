using System.Security.Cryptography;
using System.Text;

namespace Charter.Auth.Setup;

/// <summary>Why a setup token was not accepted.</summary>
public enum SetupTokenRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>No token has been issued, because setup is not required.</summary>
    NotIssued,

    /// <summary>The value did not match the issued token.</summary>
    Mismatch,

    /// <summary>The token was issued too long ago.</summary>
    Expired,

    /// <summary>The token has already created an account.</summary>
    AlreadyUsed,
}

/// <summary>
/// The one-time token of section 30.1.
/// </summary>
/// <remarks>
/// 256 bits from the OS CSPRNG, rendered base64url so it survives a copy out of a container log.
/// Not a default password, not a form anyone can find: a self-hosted instance that boots with open
/// registration gets hijacked by whoever finds it first, and the token is what closes that window.
/// </remarks>
public sealed record SetupToken(string Value, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>Long enough for an operator to find the log line and paste it; short enough to matter.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromHours(24);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    internal static SetupToken Issue(DateTimeOffset now)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return new SetupToken(value, now, now + Lifetime);
    }

    /// <summary>Constant-time comparison, so a token cannot be recovered one character at a time.</summary>
    internal bool Matches(string candidate)
        => !string.IsNullOrEmpty(candidate)
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(Value),
               Encoding.UTF8.GetBytes(candidate));
}

/// <summary>
/// Holds the current setup token for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// In memory on purpose. A token in the database would outlive the restart that reprints it and
/// would have to be revoked; a token in memory is reissued on boot and printed again, which is
/// exactly what an operator who lost the log line needs. An instance with a user already in it never
/// issues one at all.
/// </para>
/// <para>
/// Single-use is enforced here rather than by the caller: <see cref="TryConsume"/> clears the token
/// before it returns success, so a second redemption of the same value fails even if the account
/// creation that followed the first one rolled back.
/// </para>
/// </remarks>
public sealed class SetupTokenStore
{
    private readonly TimeProvider clock;
    private readonly Lock gate = new();

    private SetupToken? current;
    private bool consumed;

    public SetupTokenStore(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>The token as issued, or null when none is outstanding.</summary>
    public SetupToken? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>
    /// Issues a token, replacing an expired one.
    /// </summary>
    /// <remarks>
    /// Reissuing on expiry rather than leaving the instance unusable: the token exists to stop a
    /// stranger claiming the instance, and an operator who took more than a day to get to it should
    /// find a fresh line in the log, not a dead application.
    /// </remarks>
    public SetupToken Issue()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();

            if (current is null || current.IsExpired(now))
            {
                current = SetupToken.Issue(now);
                consumed = false;
            }

            return current;
        }
    }

    /// <summary>Redeems <paramref name="candidate"/> exactly once.</summary>
    public SetupTokenRejection TryConsume(string? candidate)
    {
        lock (gate)
        {
            if (consumed)
            {
                return SetupTokenRejection.AlreadyUsed;
            }

            if (current is not { } token)
            {
                return SetupTokenRejection.NotIssued;
            }

            if (token.IsExpired(clock.GetUtcNow()))
            {
                return SetupTokenRejection.Expired;
            }

            if (candidate is null || !token.Matches(candidate))
            {
                return SetupTokenRejection.Mismatch;
            }

            consumed = true;
            current = null;

            return SetupTokenRejection.None;
        }
    }
}
