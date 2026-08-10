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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public static Identity Create(
        Guid userId,
        IdentityProviderKind provider,
        string providerUserId,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);

        return new Identity(
            id ?? Guid.CreateVersion7(),
            userId,
            provider,
            providerUserId.Trim(),
            DomainTime.Resolve(now));
    }

    public void RecordUse(DateTimeOffset? now = null) => LastUsedAt = DomainTime.Resolve(now);
}
