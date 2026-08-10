using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>The identity providers of section 21, all behind one <c>IIdentityProvider</c> seam.</summary>
public enum IdentityProviderKind
{
    /// <summary>Email and password. Always available.</summary>
    Password,

    [EnumMember(Value = "github")]
    GitHub,

    Google,

    Discord,

    /// <summary>Doubles as the mapping that makes inbound Slack requests work (section 21).</summary>
    Slack,

    /// <summary>SAML SSO, organisation mode only.</summary>
    [EnumMember(Value = "saml")]
    Saml,
}

/// <summary>One row per linked OAuth or SAML identity (section 5).</summary>
public sealed class Identity
{
    private Identity()
    {
    }

    private Identity(
        Guid id,
        Guid userId,
        IdentityProviderKind provider,
        string providerUserId,
        DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        Provider = provider;
        ProviderUserId = providerUserId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public IdentityProviderKind Provider { get; private set; }

    /// <summary>The provider's own subject identifier, never the email address.</summary>
    public string ProviderUserId { get; private set; } = string.Empty;

    /// <summary>
    /// The verifier for a provider that authenticates against Charter itself rather than against a
    /// remote authority — today only <see cref="IdentityProviderKind.Password"/>, whose row carries
    /// the ASP.NET Core <c>PasswordHasher</c> string. Null for every federated provider, because an
    /// OAuth or SAML identity has no local secret to hold.
    /// </summary>
    /// <remarks>
    /// A hash, never a password: nothing in Charter stores or logs the plaintext (section 21). It
    /// lives on the identity row rather than on <see cref="User"/> so that a user who links several
    /// providers has one uniform place per link, and so revoking password sign-in is deleting a row.
    /// </remarks>
    public string? SecretHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public static Identity Create(
        Guid userId,
        IdentityProviderKind provider,
        string providerUserId,
        string? secretHash = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);

        return new Identity(
            id ?? Guid.CreateVersion7(),
            userId,
            provider,
            providerUserId.Trim(),
            DomainTime.Resolve(now))
        {
            SecretHash = string.IsNullOrWhiteSpace(secretHash) ? null : secretHash,
        };
    }

    /// <summary>Replaces the stored verifier. The caller hashes; the domain never sees a password.</summary>
    public void SetSecretHash(string secretHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretHash);
        SecretHash = secretHash;
    }

    public void RecordUse(DateTimeOffset? now = null) => LastUsedAt = DomainTime.Resolve(now);
}
