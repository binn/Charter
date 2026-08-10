using System.Security.Cryptography;
using System.Text;

namespace Charter.Domain;

/// <summary>Why an invitation was not redeemed.</summary>
public enum InvitationRejection
{
    /// <summary>Redeemed.</summary>
    None,

    /// <summary>No outstanding invitation matched the token.</summary>
    NotFound,

    /// <summary>The invitation was issued too long ago.</summary>
    Expired,

    /// <summary>The invitation has already created an account. Single use means single use.</summary>
    AlreadyUsed,

    /// <summary>An admin withdrew it before anybody spent it.</summary>
    Revoked,
}

/// <summary>
/// An outstanding invitation to join the organisation (section 30.2, <em>invite people</em>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The token is never stored.</strong> What this row holds is a verifier — the SHA-256
/// digest of a 256-bit token minted from the OS CSPRNG — exactly as <see cref="CredentialGrant"/>
/// holds ciphertext rather than a key and <c>RunnerAgent</c> holds a hash rather than a pairing
/// token. An invitation link is a bearer credential that creates an account: a database dump, a
/// backup or a support query must not be replayable against the redemption endpoint.
/// </para>
/// <para>
/// The digest is a plain hash rather than a PBKDF2 verifier, and deliberately so. A password is
/// low-entropy and needs a slow KDF to survive an offline attack; this token is 256 bits of CSPRNG
/// output, where the cheapest attack is guessing the whole keyspace. What a plain digest buys is the
/// indexed lookup — redemption arrives with a token and no identity, so the token has to <em>find</em>
/// its row, which a salted verifier cannot do without reading every invitation on the instance.
/// </para>
/// <para>
/// Single use is enforced on the row: <see cref="TryConsume"/> stamps <see cref="ConsumedAt"/> and a
/// second redemption of the same value is refused. The row is kept after redemption rather than
/// deleted, because "who invited this person, and when did they accept" is an audit question
/// (section 5, <c>AuditLog</c>) and a deleted row cannot answer it.
/// </para>
/// </remarks>
public sealed class Invitation
{
    /// <summary>The maximum length of an addr-spec: 64 local part, <c>@</c>, 255 domain.</summary>
    public const int MaxEmailLength = 320;

    /// <summary>A SHA-256 digest rendered as lower-case hex.</summary>
    public const int TokenHashLength = 64;

    /// <summary>
    /// Long enough for somebody to get to it after a weekend, short enough that a forwarded mail
    /// from last quarter is not a way into the instance.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private Invitation()
    {
    }

    private Invitation(
        Guid id,
        Guid orgId,
        string email,
        string tokenHash,
        IReadOnlyList<MemberRole> roles,
        Guid invitedByUserId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        OrgId = orgId;
        Email = email;
        TokenHash = tokenHash;
        Roles = roles;
        InvitedByUserId = invitedByUserId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    /// <summary>Stored lower-cased, the same normalisation <see cref="User.Email"/> uses.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The SHA-256 digest of the emailed token, in lower-case hex. Never the token.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>What they are being invited as (section 7.1). Additive, and at least one.</summary>
    public IReadOnlyList<MemberRole> Roles { get; private set; } = [];

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When it was spent. Null while the invitation is outstanding.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>The account the redemption created or linked.</summary>
    public Guid? ConsumedByUserId { get; private set; }

    /// <summary>When an admin withdrew it. Null unless somebody did.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Whether this invitation has already created an account.</summary>
    public bool IsConsumed => ConsumedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Whether it could still be redeemed at <paramref name="now"/>.</summary>
    public bool IsOutstanding(DateTimeOffset now)
        => !IsConsumed && RevokedAt is null && !IsExpired(now);

    /// <summary>
    /// Mints a token to email, and the digest to store.
    /// </summary>
    /// <remarks>
    /// 256 bits from the OS CSPRNG, rendered base64url so it survives a copy out of a mail client
    /// and a paste into a URL. The caller sends <c>Token</c> and keeps nothing; only <c>Hash</c>
    /// reaches the database.
    /// </remarks>
    public static (string Token, string Hash) MintToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (token, HashToken(token));
    }

    /// <summary>The one spelling of the digest, used by both the issuer and the lookup.</summary>
    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static Invitation Issue(
        Guid orgId,
        string email,
        Guid invitedByUserId,
        IEnumerable<MemberRole> roles,
        string tokenHash,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentNullException.ThrowIfNull(roles);

        if (tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException(
                $"An invitation stores a SHA-256 digest as {TokenHashLength} hex characters, not the token.",
                nameof(tokenHash));
        }

        IReadOnlyList<MemberRole> granted = [.. roles.Distinct().Order()];
        if (granted.Count == 0)
        {
            throw new ArgumentException("An invitation must name at least one role.", nameof(roles));
        }

        var issued = DomainTime.Resolve(now);
        var window = lifetime ?? DefaultLifetime;

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), window, "An invitation must outlive its issue.");
        }

        return new Invitation(
            id ?? Guid.CreateVersion7(),
            orgId,
            email.Trim().ToLowerInvariant(),
            tokenHash.ToLowerInvariant(),
            granted,
            invitedByUserId,
            issued,
            issued + window);
    }

    /// <summary>
    /// Redeems the invitation exactly once.
    /// </summary>
    /// <remarks>
    /// Ordered so that a spent invitation answers <see cref="InvitationRejection.AlreadyUsed"/>
    /// rather than <see cref="InvitationRejection.Expired"/>: an invitation somebody accepted a
    /// month ago is both, and the useful answer is the one that says the account already exists.
    /// </remarks>
    public InvitationRejection TryConsume(Guid userId, DateTimeOffset? now = null)
    {
        if (IsConsumed)
        {
            return InvitationRejection.AlreadyUsed;
        }

        if (RevokedAt is not null)
        {
            return InvitationRejection.Revoked;
        }

        var instant = DomainTime.Resolve(now);
        if (IsExpired(instant))
        {
            return InvitationRejection.Expired;
        }

        ConsumedAt = instant;
        ConsumedByUserId = userId;

        return InvitationRejection.None;
    }

    /// <summary>Withdraws an outstanding invitation. Idempotent; a spent one is left alone.</summary>
    public void Revoke(DateTimeOffset? now = null)
    {
        if (!IsConsumed)
        {
            RevokedAt ??= DomainTime.Resolve(now);
        }
    }
}
