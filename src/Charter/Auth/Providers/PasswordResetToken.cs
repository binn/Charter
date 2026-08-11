using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;

namespace Charter.Auth.Providers;

/// <summary>Why a password-reset link was not honoured.</summary>
public enum PasswordResetRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>The value is not a Charter reset link at all.</summary>
    Malformed,

    /// <summary>The link is past <see cref="PasswordResetTokens.Lifetime"/>.</summary>
    Expired,

    /// <summary>
    /// The signature no longer matches. Either the link was forged, or the password it was minted
    /// against has already been changed — which is what makes a reset link single-use.
    /// </summary>
    Spent,
}

/// <summary>
/// The one-time password-reset link of section 30.2, minted and verified without a table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single use comes from the signature, not from a row.</strong> The MAC covers the user id,
/// the expiry, <em>and a digest of the verifier currently stored on that user's password identity</em>.
/// Resetting the password replaces the verifier, so every link minted against the old one stops
/// verifying at the same instant. A second click on the same link is refused, and so is a link
/// somebody kept from three resets ago.
/// </para>
/// <para>
/// No table, and deliberately so: <c>Charter.Data</c> holds the schema and a reset link is worth
/// nothing after two hours. What a stored token would buy is explicit revocation, and changing the
/// password already revokes every outstanding link — which is the only revocation anybody asks for.
/// </para>
/// <para>
/// The key is <c>CHARTER_SECRET_KEY</c>, whose documented job is exactly this: <em>cookie and token
/// signing</em> (section 4.2). Rotating it invalidates outstanding links, which is the correct
/// direction to be wrong in.
/// </para>
/// </remarks>
public sealed class PasswordResetTokens
{
    /// <summary>
    /// Long enough to walk to another device for the email, short enough that a forwarded message is
    /// not a standing key to the account.
    /// </summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromHours(2);

    private const string Domain = "charter:password-reset:v1";

    private readonly byte[] key;
    private readonly TimeProvider clock;

    /// <summary>Creates the minter.</summary>
    /// <param name="secretKey"><c>CHARTER_SECRET_KEY</c>.</param>
    /// <param name="clock">The clock expiry is measured against.</param>
    public PasswordResetTokens(Secret secretKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentNullException.ThrowIfNull(clock);

        key = Encoding.UTF8.GetBytes(secretKey.Reveal());
        this.clock = clock;
    }

    /// <summary>
    /// Mints a link for <paramref name="userId"/>, bound to the verifier they have right now.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="currentSecretHash">
    /// The <c>secret_hash</c> on their password identity, or <see langword="null"/> when they have
    /// none yet — somebody invited who has only ever signed in through OAuth.
    /// </param>
    public string Issue(Guid userId, string? currentSecretHash)
    {
        var expiresAt = clock.GetUtcNow() + Lifetime;
        var payload = Payload(userId, expiresAt.ToUnixTimeSeconds());

        return string.Concat(payload, ".", Base64Url(Sign(payload, currentSecretHash)));
    }

    /// <summary>
    /// Reads the account a link names, without trusting it.
    /// </summary>
    /// <remarks>
    /// Redemption arrives with a link and no identity, so the user has to be found before the
    /// verifier that authenticates the link can be loaded. Nothing may be done on the strength of
    /// this alone; <see cref="Verify"/> is what decides.
    /// </remarks>
    public static bool TryReadSubject(string? token, out Guid userId)
    {
        userId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');

        return parts.Length == 3
               && Guid.TryParseExact(parts[0], "N", out userId);
    }

    /// <summary>Decides whether this link may set a password on this account, right now.</summary>
    public PasswordResetRejection Verify(string? token, Guid userId, string? currentSecretHash)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return PasswordResetRejection.Malformed;
        }

        var parts = token.Split('.');

        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var subject)
            || subject != userId
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return PasswordResetRejection.Malformed;
        }

        if (!TryBase64Url(parts[2], out var presented))
        {
            return PasswordResetRejection.Malformed;
        }

        // The signature is checked before the expiry, so a forged token never learns from the answer
        // which half it got wrong.
        var expected = Sign(Payload(subject, expiresUnix), currentSecretHash);

        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            return PasswordResetRejection.Spent;
        }

        return clock.GetUtcNow().ToUnixTimeSeconds() >= expiresUnix
            ? PasswordResetRejection.Expired
            : PasswordResetRejection.None;
    }

    /// <summary>The sentence to show for a refusal. Never says which account it was about.</summary>
    public static string Describe(PasswordResetRejection rejection) => rejection switch
    {
        PasswordResetRejection.Expired =>
            "That reset link has expired. Ask for a new one.",
        PasswordResetRejection.Spent =>
            "That reset link has already been used. Ask for a new one.",
        _ =>
            "That reset link is not one we recognise. Ask for a new one.",
    };

    private static string Payload(Guid userId, long expiresUnix)
        => string.Create(CultureInfo.InvariantCulture, $"{userId:N}.{expiresUnix}");

    private byte[] Sign(string payload, string? currentSecretHash)
    {
        // The verifier is folded in as a digest rather than raw, so nothing derived from a stored
        // hash travels in a URL even in principle.
        var binding = SHA256.HashData(Encoding.UTF8.GetBytes(currentSecretHash ?? string.Empty));

        var message = new byte[Encoding.UTF8.GetByteCount(Domain) + 1 + Encoding.UTF8.GetByteCount(payload) + 1 + binding.Length];
        var written = Encoding.UTF8.GetBytes(Domain, message);

        message[written++] = 0x1f;
        written += Encoding.UTF8.GetBytes(payload, message.AsSpan(written));
        message[written++] = 0x1f;
        binding.CopyTo(message.AsSpan(written));

        return HMACSHA256.HashData(key, message);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64Url(string value, out byte[] bytes)
    {
        bytes = [];

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => string.Empty,
        };

        if (padded.Length == 0)
        {
            return false;
        }

        var buffer = new byte[padded.Length / 4 * 3];

        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
