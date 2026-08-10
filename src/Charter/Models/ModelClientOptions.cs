namespace Charter.Models;

/// <summary>
/// Everything the model layer needs from configuration.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a record owned by this namespace rather than a slice of <c>CharterConfig</c>. The
/// model layer has no business knowing how configuration is parsed, and the host can adapt one to
/// the other in a single expression at startup - section 4.1's immutable, parsed-once contract is
/// unaffected.
/// </para>
/// <para>
/// The defaults here mirror section 4.2 exactly. Nothing in this record is a secret: instance-level
/// keys arrive as <see cref="ModelCredential"/> values through <see cref="IModelCredentialStore"/>,
/// so an options object can safely be logged.
/// </para>
/// </remarks>
public sealed record ModelClientOptions
{
    /// <summary>Section 4.2 <c>CHARTER_MODEL_REFINE</c>. Default <c>claude-sonnet-5</c>.</summary>
    public ModelIdentifier RefineModel { get; init; } = ModelIdentifier.Parse("claude-sonnet-5");

    /// <summary>Section 4.2 <c>CHARTER_MODEL_BUILD</c>. Default <c>claude-opus-5</c>.</summary>
    public ModelIdentifier BuildModel { get; init; } = ModelIdentifier.Parse("claude-opus-5");

    /// <summary>Section 4.2 <c>CHARTER_MODEL_TEACH</c>. Default <c>claude-sonnet-5</c>.</summary>
    public ModelIdentifier TeachModel { get; init; } = ModelIdentifier.Parse("claude-sonnet-5");

    /// <summary>Default failover policy for repos that have not set one. Section 20b.4.</summary>
    public ModelFailoverPolicy FailoverPolicy { get; init; } = ModelFailoverPolicy.PauseAndResume;

    /// <summary>Per-request timeout for control-plane calls.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the OpenRouter model catalog stays fresh before it is refetched. Section 20b.6.
    /// </summary>
    public TimeSpan OpenRouterCatalogTtl { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The OpenRouter models endpoint. Overridable for a proxy or a test double.</summary>
    public Uri OpenRouterModelsEndpoint { get; init; } = new("https://openrouter.ai/api/v1/models");

    /// <summary>
    /// Sent to OpenRouter as <c>HTTP-Referer</c> for attribution. Charter's own public URL, not a
    /// requester's.
    /// </summary>
    public Uri? OpenRouterReferer { get; init; }

    /// <summary>Sent to OpenRouter as <c>X-Title</c> for attribution.</summary>
    public string OpenRouterTitle { get; init; } = "Charter";

    /// <summary>
    /// Default endpoints per provider, used when a credential carries no
    /// <see cref="ModelCredential.BaseUrl"/>.
    /// </summary>
    public IReadOnlyDictionary<ModelProvider, Uri> DefaultBaseUrls { get; init; } = DefaultEndpoints;

    /// <summary>The public endpoints Charter ships with.</summary>
    public static IReadOnlyDictionary<ModelProvider, Uri> DefaultEndpoints { get; } =
        new Dictionary<ModelProvider, Uri>
        {
            [ModelProvider.Anthropic] = new Uri("https://api.anthropic.com"),
            [ModelProvider.OpenAi] = new Uri("https://api.openai.com/v1"),
            [ModelProvider.OpenRouter] = new Uri("https://openrouter.ai/api/v1"),
            [ModelProvider.XAi] = new Uri("https://api.x.ai/v1"),
            [ModelProvider.DeepSeek] = new Uri("https://api.deepseek.com/v1"),
            [ModelProvider.Groq] = new Uri("https://api.groq.com/openai/v1"),
            [ModelProvider.Ollama] = new Uri("http://localhost:11434/v1"),
            [ModelProvider.Google] = new Uri("https://generativelanguage.googleapis.com/v1beta"),
        };

    /// <summary>
    /// Resolves the endpoint for a credential: its own <c>base_url</c> if it has one, else the
    /// provider default. Azure OpenAI and custom OpenAI-compatible endpoints have no default and
    /// must supply one.
    /// </summary>
    /// <exception cref="ModelClientException">No base URL is available for the provider.</exception>
    public Uri ResolveBaseUrl(ModelCredential credential, ModelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (credential.BaseUrl is not null)
        {
            return credential.BaseUrl;
        }

        if (DefaultBaseUrls.TryGetValue(provider, out var configured))
        {
            return configured;
        }

        if (DefaultEndpoints.TryGetValue(provider, out var shipped))
        {
            return shipped;
        }

        throw new ModelClientException(
            $"No base URL is configured for provider '{provider}'. Set base_url on the credential.",
            provider);
    }
}
