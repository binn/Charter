using System.Text.Json.Serialization;

namespace Charter.Api.Credentials;

/// <summary>The credential kinds of section 20b.2, in the client's spelling.</summary>
/// <remarks>
/// Spelled explicitly rather than left to the API's <c>snake_case</c> converter, because the
/// converter would split the capitals inside a vendor's name and produce <c>open_router_key</c> and
/// <c>open_ai_oauth</c>. Section 20b.2 writes <c>openrouter_key</c> and <c>openai_oauth</c>, the
/// adapter YAML uses the same spelling, and a wire vocabulary that disagrees with both is a bug
/// waiting for the first person who copies a value out of the specification.
/// </remarks>
public enum ApiCredentialKind
{
    [JsonStringEnumMemberName("anthropic_oauth")]
    AnthropicOauth,

    [JsonStringEnumMemberName("anthropic_api_key")]
    AnthropicApiKey,

    [JsonStringEnumMemberName("openai_oauth")]
    OpenAiOauth,

    [JsonStringEnumMemberName("openai_api_key")]
    OpenAiApiKey,

    [JsonStringEnumMemberName("google_api_key")]
    GoogleApiKey,

    [JsonStringEnumMemberName("xai_api_key")]
    XaiApiKey,

    [JsonStringEnumMemberName("openrouter_key")]
    OpenRouterKey,

    [JsonStringEnumMemberName("cursor_api_key")]
    CursorApiKey,

    [JsonStringEnumMemberName("custom_openai_compatible")]
    CustomOpenAiCompatible,
}

/// <summary>Personal or pooled (section 20b.2).</summary>
public enum ApiCredentialScope
{
    Personal,
    SharedPool,
}

/// <summary>Grant lifecycle state (section 20b.2).</summary>
public enum ApiCredentialStatus
{
    Active,
    Exhausted,
    Invalid,
    Revoked,
}

/// <summary>Where a credential came from.</summary>
/// <remarks>
/// The distinction is operationally load-bearing rather than cosmetic. A <c>grant</c> is a row and
/// can be revoked over HTTP; an <c>environment</c> credential is <c>ANTHROPIC_API_KEY</c> or
/// <c>OPENROUTER_API_KEY</c> and changes only by editing the deployment and restarting it. Showing
/// both in one list is the point: an operator asking "what can this instance authenticate with"
/// needs the answer to include the variables, which is the fact whose absence made the whole
/// resolution chain look empty.
/// </remarks>
public enum ApiCredentialSource
{
    Grant,
    Environment,
}

/// <summary>
/// One credential, as the credentials screen shows it.
/// </summary>
/// <remarks>
/// Section 20b.2: <em>never return a token to the UI after creation</em>. There is no secret property
/// on this type and there must never be one — provider, owner, status and last used are the whole of
/// what a person needs, and a reveal endpoint is the thing this record exists to make impossible.
/// </remarks>
public sealed record CredentialResponse
{
    /// <summary>The grant id, or the environment variable name for an instance-level credential.</summary>
    public required string Id { get; init; }

    /// <summary>Whether this is a stored grant or an environment variable.</summary>
    public required ApiCredentialSource Source { get; init; }

    /// <summary>The environment variable, when <see cref="Source"/> is <c>environment</c>.</summary>
    public string? Variable { get; init; }

    /// <summary>Which provider it authenticates against.</summary>
    public required ApiCredentialKind Kind { get; init; }

    /// <summary>Personal or pooled. Always personal for an instance-level credential.</summary>
    public required ApiCredentialScope Scope { get; init; }

    /// <summary>Lifecycle state.</summary>
    public required ApiCredentialStatus Status { get; init; }

    /// <summary>The owning member's display name, where the caller may see it.</summary>
    public string? OwnerName { get; init; }

    /// <summary>The owning user's id, for a grant.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>A per-credential endpoint override, for a gateway or a self-hosted model.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Shared-pool ordering. Higher wins (section 20b.3).</summary>
    public int? Priority { get; init; }

    /// <summary>The owner's cap on other people's sessions per day (section 20b.5).</summary>
    public int? MaxSessionsPerDayFromOthers { get; init; }

    /// <summary>Whether an overflow allowance is configured (section 20b.3, tier 2).</summary>
    public bool? OverflowEnabled { get; init; }

    /// <summary>When capacity returns, if a provider said (section 20b.4).</summary>
    public DateTimeOffset? ExhaustedUntil { get; init; }

    /// <summary>Why it was rejected, in short prose. Never any part of the credential.</summary>
    public string? InvalidReason { get; init; }

    /// <summary>OAuth access-token expiry, where the grant is OAuth-backed.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When it was created. Absent for an environment credential, which has no such moment.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The last successful call made with it (section 20b.2).</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>The credentials screen: everything this instance can authenticate with.</summary>
public sealed record CredentialListResponse
{
    /// <summary>Stored grants and instance-level keys together, newest grant first.</summary>
    public required IReadOnlyList<CredentialResponse> Credentials { get; init; }

    /// <summary><c>CHARTER_ALLOW_SHARED_POOL</c> (section 20b.7). Read-only here; it is a variable.</summary>
    public required bool SharedPoolAllowed { get; init; }

    /// <summary>
    /// The models this instance is configured to call, so the screen can say whether anything present
    /// can actually serve them (sections 4.2, 20b.1).
    /// </summary>
    public required IReadOnlyList<CredentialModelResponse> Models { get; init; }
}

/// <summary>One configured control-plane model and whether a credential can serve it.</summary>
/// <param name="Purpose">Which variable selects it: <c>refine</c> or <c>teach</c>.</param>
/// <param name="Model">The provider-qualified identifier.</param>
/// <param name="Servable">Whether any active credential in the chain could serve it.</param>
/// <param name="Remedy">What to configure when it cannot be served. Absent when it can.</param>
public sealed record CredentialModelResponse(
    string Purpose,
    string Model,
    bool Servable,
    string? Remedy);

/// <summary>Body of <c>POST /api/credentials</c>.</summary>
/// <remarks>
/// The secret arrives once and is encrypted before it reaches the database (section 20b.2). It is
/// never echoed back, never logged, and no route returns it afterwards.
/// </remarks>
public sealed class CreateCredentialBody
{
    /// <summary>Which provider this authenticates against.</summary>
    public ApiCredentialKind? Kind { get; set; }

    /// <summary>The key or token. Required, and write-only.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Personal (the default) or shared pool. Opting into the pool is an explicit action and carries
    /// the section 20b.7 caution.
    /// </summary>
    public ApiCredentialScope? Scope { get; set; }

    /// <summary>A per-credential endpoint, required for a custom OpenAI-compatible provider.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Shared-pool ordering. Higher wins.</summary>
    public int? Priority { get; set; }

    /// <summary>The owner's cap on other people's sessions per day (section 20b.5).</summary>
    public int? MaxSessionsPerDayFromOthers { get; set; }
}

/// <summary>What <c>POST /api/credentials</c> answers with — the credential, and never the key.</summary>
/// <param name="Credential">The stored grant as the list renders it.</param>
/// <param name="Warning">
/// The section 20b.7 one-time caution, present only when the credential was opted into a shared pool.
/// </param>
public sealed record CreateCredentialResponse(CredentialResponse Credential, string? Warning);
