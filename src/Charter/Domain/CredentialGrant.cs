using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Charter.Domain;

/// <summary>The credential kinds of section 20b.2.</summary>
public enum CredentialKind
{
    [EnumMember(Value = "anthropic_oauth")]
    AnthropicOauth,

    AnthropicApiKey,

    [EnumMember(Value = "openai_oauth")]
    OpenAiOauth,

    [EnumMember(Value = "openai_api_key")]
    OpenAiApiKey,

    GoogleApiKey,

    [EnumMember(Value = "xai_api_key")]
    XaiApiKey,

    [EnumMember(Value = "openrouter_key")]
    OpenRouterKey,

    [EnumMember(Value = "custom_openai_compatible")]
    CustomOpenAiCompatible,
}

/// <summary>Whether a grant is private to its owner or pooled across the organisation.</summary>
public enum CredentialScope
{
    Personal,

    /// <summary>
    /// Opted in explicitly, never by default (section 20b.5), and carrying the terms-of-service
    /// caution of section 20b.7.
    /// </summary>
    SharedPool,
}

/// <summary>Section 20b.2. Exhausted and invalid grants are skipped by the resolution chain.</summary>
public enum CredentialStatus
{
    Active,

    /// <summary>A 429 landed. <see cref="CredentialGrant.ExhaustedUntil"/> holds the reset instant.</summary>
    Exhausted,

    Invalid,

    Revoked,
}

/// <summary>
/// A stored model credential (section 20b.2). Secrets are ciphertext here and nowhere else.
/// </summary>
/// <remarks>
/// Handling rules, all of them load-bearing:
/// <list type="bullet">
///   <item>Encrypted at rest with the dedicated <c>CHARTER_CREDENTIAL_KEY</c>, never
///         <c>CHARTER_SECRET_KEY</c>, so rotating cookie signing does not invalidate every stored
///         credential.</item>
///   <item>There is no plaintext property on this type, and there must never be one. The ciphertext
///         columns are <see cref="JsonIgnore"/>d so a careless serialisation cannot leak them, and
///         no read model exposes them.</item>
///   <item>Never logged, and never returned to the UI after creation — the UI shows provider, owner,
///         status and last used.</item>
///   <item>The control plane owns OAuth refresh. Runners receive a short-TTL access token only,
///         never a refresh token (sections 7.4, 33.5).</item>
/// </list>
/// </remarks>
public sealed class CredentialGrant
{
    private CredentialGrant()
    {
    }

    private CredentialGrant(
        Guid id,
        Guid orgId,
        Guid ownerUserId,
        CredentialKind kind,
        CredentialScope scope,
        byte[] secretEncrypted,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        OwnerUserId = ownerUserId;
        Kind = kind;
        Scope = scope;
        SecretEncrypted = secretEncrypted;
        Status = CredentialStatus.Active;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public CredentialKind Kind { get; private set; }

    /// <summary>For self-hosted or proxied endpoints, including <c>ANTHROPIC_BASE_URL</c> gateways.</summary>
    public string? BaseUrl { get; private set; }

    public CredentialScope Scope { get; private set; }

    /// <summary>Ciphertext. Never decrypted by this type, never rendered, never logged.</summary>
    [JsonIgnore]
    public byte[] SecretEncrypted { get; private set; } = [];

    /// <summary>Ciphertext, and it never leaves the control plane (section 20b.2).</summary>
    [JsonIgnore]
    public byte[]? RefreshTokenEncrypted { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public CredentialStatus Status { get; private set; }

    /// <summary>Taken from the provider's reset header. Do not blind-retry (section 20b.4).</summary>
    public DateTimeOffset? ExhaustedUntil { get; private set; }

    /// <summary>Order within the shared pool. Higher wins.</summary>
    public int Priority { get; private set; }

    /// <summary>Caps the owner's exposure when pooled (section 20b.5).</summary>
    public int? MaxSessionsPerDayFromOthers { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Section 20b.3 skips anything exhausted or invalid when resolving a session.</summary>
    public bool IsUsableAt(DateTimeOffset now) => Status switch
    {
        CredentialStatus.Active => ExpiresAt is null || ExpiresAt > now,
        CredentialStatus.Exhausted => ExhaustedUntil is not null && ExhaustedUntil <= now,
        _ => false,
    };

    public static CredentialGrant Create(
        Guid orgId,
        Guid ownerUserId,
        CredentialKind kind,
        byte[] secretEncrypted,
        CredentialScope scope = CredentialScope.Personal,
        byte[]? refreshTokenEncrypted = null,
        string? baseUrl = null,
        DateTimeOffset? expiresAt = null,
        int priority = 0,
        int? maxSessionsPerDayFromOthers = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(secretEncrypted);
        if (secretEncrypted.Length == 0)
        {
            throw new ArgumentException("A credential grant must carry ciphertext.", nameof(secretEncrypted));
        }

        if (maxSessionsPerDayFromOthers is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSessionsPerDayFromOthers),
                maxSessionsPerDayFromOthers,
                "A daily cap cannot be negative.");
        }

        return new CredentialGrant(
            id ?? Guid.CreateVersion7(),
            orgId,
            ownerUserId,
            kind,
            scope,
            secretEncrypted,
            DomainTime.Resolve(now))
        {
            RefreshTokenEncrypted = refreshTokenEncrypted,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
            ExpiresAt = DomainTime.ResolveOptional(expiresAt),
            Priority = priority,
            MaxSessionsPerDayFromOthers = maxSessionsPerDayFromOthers,
        };
    }

    /// <summary>Rotates the stored ciphertext after an OAuth refresh. Still never plaintext.</summary>
    public void ReplaceSecret(byte[] secretEncrypted, byte[]? refreshTokenEncrypted, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(secretEncrypted);
        if (secretEncrypted.Length == 0)
        {
            throw new ArgumentException("A credential grant must carry ciphertext.", nameof(secretEncrypted));
        }

        SecretEncrypted = secretEncrypted;
        RefreshTokenEncrypted = refreshTokenEncrypted ?? RefreshTokenEncrypted;
        ExpiresAt = DomainTime.ResolveOptional(expiresAt);
        Status = CredentialStatus.Active;
        ExhaustedUntil = null;
    }

    /// <summary>Section 20b.4: a 429 marks the grant exhausted until the provider's reset instant.</summary>
    public void MarkExhausted(DateTimeOffset until)
    {
        Status = CredentialStatus.Exhausted;
        ExhaustedUntil = until.ToUniversalTime();
    }

    public void MarkInvalid()
    {
        Status = CredentialStatus.Invalid;
        ExhaustedUntil = null;
    }

    /// <summary>Immediate, and it kills in-flight sessions using this grant (section 20b.2).</summary>
    public void Revoke() => Status = CredentialStatus.Revoked;

    /// <summary>Opting into the pool is an explicit action and carries a one-time warning.</summary>
    public void JoinSharedPool() => Scope = CredentialScope.SharedPool;

    /// <summary>One-click withdrawal (section 20b.5).</summary>
    public void LeaveSharedPool() => Scope = CredentialScope.Personal;

    public void RecordUse(DateTimeOffset? now = null) => LastUsedAt = DomainTime.Resolve(now);
}
