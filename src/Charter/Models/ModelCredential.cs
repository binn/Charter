namespace Charter.Models;

/// <summary>The kinds of credential a <c>CredentialGrant</c> can hold. Mirrors section 20b.2.</summary>
public enum ModelCredentialKind
{
    /// <summary>A linked Anthropic subscription. Consumes quota, not dollars.</summary>
    AnthropicOAuth,

    /// <summary>A metered Anthropic API key.</summary>
    AnthropicApiKey,

    /// <summary>A linked OpenAI subscription. Consumes quota, not dollars.</summary>
    OpenAiOAuth,

    /// <summary>A metered OpenAI API key.</summary>
    OpenAiApiKey,

    /// <summary>A metered Google AI API key.</summary>
    GoogleApiKey,

    /// <summary>A metered xAI API key.</summary>
    XaiApiKey,

    /// <summary>A metered OpenRouter key. The last resort of the resolution chain.</summary>
    OpenRouterKey,

    /// <summary>
    /// Any other endpoint speaking <c>/chat/completions</c> at a configured
    /// <see cref="ModelCredential.BaseUrl"/> - a self-hosted gateway, Groq, DeepSeek, Ollama.
    /// </summary>
    CustomOpenAiCompatible,
}

/// <summary>Whether a grant is private to its owner or pooled. Section 20b.2.</summary>
public enum ModelCredentialScope
{
    /// <summary>Usable only by its owner.</summary>
    Personal,

    /// <summary>
    /// Explicitly opted into the organisation's shared pool by its owner. Never a default
    /// (section 20b.5, section 20b.7).
    /// </summary>
    SharedPool,
}

/// <summary>Grant lifecycle state. Section 20b.2.</summary>
public enum ModelCredentialStatus
{
    /// <summary>Usable.</summary>
    Active,

    /// <summary>
    /// Rate limited. Skipped by the resolution chain until <see cref="ModelCredential.ExhaustedUntil"/>.
    /// </summary>
    Exhausted,

    /// <summary>Authentication failed. Skipped until a human fixes it.</summary>
    Invalid,

    /// <summary>Withdrawn. Revocation is immediate and kills in-flight sessions using the grant.</summary>
    Revoked,
}

/// <summary>
/// Extra or overflow usage attached to a subscription grant - tier 2 of the section 20b.3 chain.
/// </summary>
public sealed record ModelCredentialOverflow
{
    /// <summary>Whether the owner has any overflow allowance configured at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>Overflow lifecycle state, tracked separately from the subscription itself.</summary>
    public ModelCredentialStatus Status { get; init; } = ModelCredentialStatus.Active;

    /// <summary>When the overflow allowance resets, if it is exhausted.</summary>
    public DateTimeOffset? ExhaustedUntil { get; init; }
}

/// <summary>
/// A <c>CredentialGrant</c> as the model layer needs to see it: enough to pick one, use it and
/// attribute the spend, and nothing more.
/// </summary>
/// <remarks>
/// Deliberately not an EF entity. The persistence layer owns the row, the encryption and the
/// lifecycle; the orchestrator projects rows onto this record - decrypting
/// <see cref="Secret"/> on the way through - when it implements
/// <see cref="IModelCredentialStore"/>.
/// </remarks>
public sealed record ModelCredential
{
    /// <summary>The grant's identifier, opaque to this layer.</summary>
    public required string Id { get; init; }

    /// <summary>Which provider the grant authenticates against.</summary>
    public required ModelCredentialKind Kind { get; init; }

    /// <summary>The decrypted secret. Never logged, never returned to the UI.</summary>
    public required ModelSecret Secret { get; init; }

    /// <summary>The owning user, or <see langword="null"/> for an organisation-level grant.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>The owning organisation.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// A per-credential endpoint override, for self-hosted or proxied deployments. Section 20b.2
    /// <c>base_url</c>. When unset the client uses the provider's public endpoint.
    /// </summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>Personal or pooled.</summary>
    public ModelCredentialScope Scope { get; init; } = ModelCredentialScope.Personal;

    /// <summary>Lifecycle state.</summary>
    public ModelCredentialStatus Status { get; init; } = ModelCredentialStatus.Active;

    /// <summary>
    /// When a rate limit resets, recorded from the reset header on the <c>429</c> that exhausted the
    /// grant. Section 20b.4.
    /// </summary>
    public DateTimeOffset? ExhaustedUntil { get; init; }

    /// <summary>Shared-pool ordering. Lower values are tried first. Section 20b.3.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// The owner's cap on how many sessions other people may run against this grant per day.
    /// Section 20b.5. Enforced by the caller; surfaced here so it can be.
    /// </summary>
    public int? MaxSessionsPerDayFromOthers { get; init; }

    /// <summary>Extra or overflow usage on a subscription grant.</summary>
    public ModelCredentialOverflow? Overflow { get; init; }

    /// <summary>OAuth access-token expiry, where the grant is OAuth-backed.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Whether the grant is subscription-backed, and so charged in quota rather than dollars.</summary>
    public bool IsSubscription =>
        Kind is ModelCredentialKind.AnthropicOAuth or ModelCredentialKind.OpenAiOAuth;

    /// <summary>The transport a client must use for this grant.</summary>
    public ModelProvider Provider => Kind switch
    {
        ModelCredentialKind.AnthropicOAuth or ModelCredentialKind.AnthropicApiKey => ModelProvider.Anthropic,
        ModelCredentialKind.OpenAiOAuth or ModelCredentialKind.OpenAiApiKey => ModelProvider.OpenAi,
        ModelCredentialKind.GoogleApiKey => ModelProvider.Google,
        ModelCredentialKind.XaiApiKey => ModelProvider.XAi,
        ModelCredentialKind.OpenRouterKey => ModelProvider.OpenRouter,
        ModelCredentialKind.CustomOpenAiCompatible => ModelProvider.OpenAiCompatible,
        _ => ModelProvider.OpenAiCompatible,
    };

    /// <summary>
    /// Never renders the secret. Overridden because a positional or generated record
    /// <c>ToString</c> would otherwise print every property, and one of them is a credential.
    /// </summary>
    public override string ToString() =>
        $"CredentialGrant {{ Id = {Id}, Kind = {Kind}, Scope = {Scope}, Status = {Status}, " +
        $"Secret = {ModelSecret.RedactedPlaceholder} }}";
}
