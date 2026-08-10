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

    /// <summary>
    /// A Cursor account key, read by the <c>cursor-agent</c> CLI (section 12b).
    /// </summary>
    /// <remarks>
    /// Listed in section 20b.2 and in <c>Charter.Adapters.AdapterCredentialKinds</c>. It authenticates
    /// against a Cursor account rather than against a model provider, so it buys an <em>agent run</em>
    /// and never a control-plane call: no <c>IModelClient</c> speaks to Cursor, and the credential
    /// store therefore never offers this kind as a candidate for a refinement, teaching or recap call.
    /// </remarks>
    CursorApiKey,

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

    /// <summary>
    /// A 429 landed. <see cref="CredentialGrant.ExhaustedUntil"/> holds the reset instant the provider
    /// gave, or <see langword="null"/> when it gave none — see
    /// <see cref="CredentialGrant.IsExhaustedIndefinitely"/>.
    /// </summary>
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
    /// <summary>How much of an invalidation reason is kept. Long enough to read, short enough to cap.</summary>
    public const int MaxInvalidReasonLength = 300;

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

    /// <summary>
    /// Taken from the provider's reset header, or <see langword="null"/> when there was none. Do not
    /// blind-retry (section 20b.4).
    /// </summary>
    /// <remarks>
    /// Nullable in the column and nullable here, and the two nulls mean different things depending on
    /// <see cref="Status"/>: not exhausted at all, or exhausted with no known reset. A far-future
    /// sentinel would collapse the second case into the first for anything that renders the value —
    /// section 20b.3 puts this instant in front of a requester as <em>waiting for capacity</em>, and
    /// "waiting for capacity until the year 9999" is not a thing to show a person.
    /// </remarks>
    public DateTimeOffset? ExhaustedUntil { get; private set; }

    /// <summary>
    /// Exhausted with no reset instant: it stays skipped until a refresh, a re-link, or an operator
    /// clears it, rather than recovering on a timer.
    /// </summary>
    public bool IsExhaustedIndefinitely => Status == CredentialStatus.Exhausted && ExhaustedUntil is null;

    /// <summary>
    /// Why the grant was marked <see cref="CredentialStatus.Invalid"/>, in short human-readable prose.
    /// </summary>
    /// <remarks>
    /// An admin looking at a dead credential needs to know whether the provider rejected the key, the
    /// subscription lapsed, or the refresh token was withdrawn — "invalid" on its own sends them to
    /// the logs. Never credential material: callers pass a reason, never a token, and the value is
    /// trimmed and truncated to <see cref="MaxInvalidReasonLength"/> so a stray payload cannot be
    /// smuggled in whole.
    /// </remarks>
    public string? InvalidReason { get; private set; }

    /// <summary>
    /// Whether the owner has extra or overflow usage on top of the subscription — tier 2 of the
    /// section 20b.3 chain.
    /// </summary>
    public bool OverflowEnabled { get; private set; }

    /// <summary>
    /// The overflow allowance's own lifecycle state, tracked separately from the subscription.
    /// </summary>
    /// <remarks>
    /// Separate because the two run out separately: the monthly subscription quota can be spent while
    /// pay-as-you-go overflow still has room, and the overflow can be capped while the subscription
    /// resets on its own schedule. One status column would force the resolver to guess which of the
    /// two the 429 belonged to, and guessing wrong either burns a working allowance or retries a dead
    /// one.
    /// </remarks>
    public CredentialStatus OverflowStatus { get; private set; } = CredentialStatus.Active;

    /// <summary>When the overflow allowance resets, if it is exhausted and the provider said.</summary>
    public DateTimeOffset? OverflowExhaustedUntil { get; private set; }

    /// <summary>Order within the shared pool. Higher wins.</summary>
    public int Priority { get; private set; }

    /// <summary>Caps the owner's exposure when pooled (section 20b.5).</summary>
    public int? MaxSessionsPerDayFromOthers { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Section 20b.3 skips anything exhausted or invalid when resolving a session.</summary>
    /// <remarks>
    /// An exhausted grant with no recorded reset is <em>not</em> usable at any instant: nothing is
    /// known about when capacity returns, so the honest answer is no until something clears it. An
    /// expired access token stops it at any status, because the reset it is waiting for is a quota's,
    /// not a token's.
    /// </remarks>
    public bool IsUsableAt(DateTimeOffset now)
    {
        if (ExpiresAt is { } expiry && expiry <= now)
        {
            return false;
        }

        return Status switch
        {
            CredentialStatus.Active => true,
            CredentialStatus.Exhausted => ExhaustedUntil is { } until && until <= now,
            _ => false,
        };
    }

    /// <summary>
    /// Whether tier 2 of section 20b.3 — the overflow allowance on this grant — can serve a session.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of <see cref="IsUsableAt"/>: the resolver reaches this tier precisely
    /// because the primary quota was unusable. An expired or revoked grant still fails, though, since
    /// overflow is spent through the same credential.
    /// </remarks>
    public bool IsOverflowUsableAt(DateTimeOffset now)
    {
        if (!OverflowEnabled || Status is CredentialStatus.Revoked or CredentialStatus.Invalid)
        {
            return false;
        }

        if (ExpiresAt is { } expiry && expiry <= now)
        {
            return false;
        }

        return OverflowStatus switch
        {
            CredentialStatus.Active => true,
            CredentialStatus.Exhausted => OverflowExhaustedUntil is { } until && until <= now,
            _ => false,
        };
    }

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
        bool overflowEnabled = false,
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
            OverflowEnabled = overflowEnabled,
        };
    }

    /// <summary>Records that the owner has extra or overflow usage available (section 20b.3, tier 2).</summary>
    public void EnableOverflow()
    {
        OverflowEnabled = true;
        OverflowStatus = CredentialStatus.Active;
        OverflowExhaustedUntil = null;
    }

    /// <summary>Withdraws the overflow allowance. Tier 2 stops being reachable for this grant.</summary>
    public void DisableOverflow()
    {
        OverflowEnabled = false;
        OverflowStatus = CredentialStatus.Active;
        OverflowExhaustedUntil = null;
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
        InvalidReason = null;
        OverflowStatus = CredentialStatus.Active;
        OverflowExhaustedUntil = null;
    }

    /// <summary>
    /// Section 20b.4: a 429 marks the grant exhausted until the provider's reset instant.
    /// </summary>
    /// <param name="until">
    /// The reset instant from the provider's header, or <see langword="null"/> when it sent none — in
    /// which case the grant is exhausted indefinitely and recovers only when something clears it.
    /// </param>
    public void MarkExhausted(DateTimeOffset? until)
    {
        Status = CredentialStatus.Exhausted;
        ExhaustedUntil = DomainTime.ResolveOptional(until);
    }

    /// <summary>
    /// Exhausts the overflow allowance alone, leaving the subscription's own state where it was.
    /// </summary>
    /// <param name="until">The reset instant, or <see langword="null"/> for indefinitely.</param>
    public void MarkOverflowExhausted(DateTimeOffset? until)
    {
        OverflowStatus = CredentialStatus.Exhausted;
        OverflowExhaustedUntil = DomainTime.ResolveOptional(until);
    }

    /// <summary>
    /// Marks the grant invalid after a hard authentication failure, recording why.
    /// </summary>
    /// <param name="reason">
    /// Short human-readable prose for an admin. Never credential material — the reason is stored in
    /// clear and shown in the UI.
    /// </param>
    public void MarkInvalid(string? reason = null)
    {
        Status = CredentialStatus.Invalid;
        ExhaustedUntil = null;
        InvalidReason = NormaliseReason(reason);

        // A credential the provider rejects outright cannot spend its overflow either: overflow is
        // billed through the same account and presented with the same secret.
        OverflowStatus = CredentialStatus.Invalid;
        OverflowExhaustedUntil = null;
    }

    /// <summary>Immediate, and it kills in-flight sessions using this grant (section 20b.2).</summary>
    public void Revoke()
    {
        Status = CredentialStatus.Revoked;
        OverflowStatus = CredentialStatus.Revoked;
    }

    /// <summary>Opting into the pool is an explicit action and carries a one-time warning.</summary>
    public void JoinSharedPool() => Scope = CredentialScope.SharedPool;

    /// <summary>One-click withdrawal (section 20b.5).</summary>
    public void LeaveSharedPool() => Scope = CredentialScope.Personal;

    public void RecordUse(DateTimeOffset? now = null) => LastUsedAt = DomainTime.Resolve(now);

    private static string? NormaliseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxInvalidReasonLength
            ? trimmed
            : trimmed[..MaxInvalidReasonLength];
    }
}
