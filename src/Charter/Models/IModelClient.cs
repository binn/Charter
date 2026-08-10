using System.Runtime.CompilerServices;

namespace Charter.Models;

/// <summary>
/// A control-plane model client. Section 20b.1.
/// </summary>
/// <remarks>
/// <para>
/// This covers refinement, teaching, recap and recon only - the calls Charter itself makes over its
/// own HTTP client. Agent runs authenticate differently: env vars injected into the runner process
/// and consumed by the agent CLI, never through this interface. Section 20b intro calls conflating
/// the two the main implementation hazard here.
/// </para>
/// <para>
/// The credential is a per-call argument rather than constructor state because it is resolved per
/// session (section 20b.3), while clients are singletons.
/// </para>
/// </remarks>
public interface IModelClient
{
    /// <summary>The transport this client primarily speaks.</summary>
    ModelProvider Provider { get; }

    /// <summary>
    /// Every provider this client can serve. Most providers are OpenAI-compatible, so one client
    /// covers many entries here.
    /// </summary>
    IReadOnlyCollection<ModelProvider> SupportedProviders { get; }

    /// <summary>Whether this client can serve the given model identifier.</summary>
    bool Supports(ModelIdentifier model);

    /// <summary>Runs a request to completion.</summary>
    /// <exception cref="ModelRateLimitException">The provider returned <c>429</c>.</exception>
    /// <exception cref="ModelAuthenticationException">The provider rejected the credential.</exception>
    /// <exception cref="ModelClientException">Any other provider failure.</exception>
    Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a request. The final event is always
    /// <see cref="ModelStreamEvent.Completed"/>, carrying the assembled text, usage and cost.
    /// </summary>
    /// <exception cref="ModelRateLimitException">The provider returned <c>429</c>.</exception>
    /// <exception cref="ModelAuthenticationException">The provider rejected the credential.</exception>
    /// <exception cref="ModelClientException">Any other provider failure.</exception>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves a provider to the client that speaks it. Section 20b.1.</summary>
public interface IModelClientFactory
{
    /// <summary>Returns the client for a model identifier.</summary>
    /// <exception cref="ModelClientException">No client is registered for the provider.</exception>
    IModelClient GetClient(ModelIdentifier model);

    /// <summary>Returns the client for a provider.</summary>
    /// <exception cref="ModelClientException">No client is registered for the provider.</exception>
    IModelClient GetClient(ModelProvider provider);

    /// <summary>Returns the client for a provider, or <see langword="null"/> if none is registered.</summary>
    IModelClient? Find(ModelProvider provider);
}

/// <summary>Convenience wrappers over <see cref="IModelClient"/> for the single-shot callers.</summary>
public static class ModelClientExtensions
{
    /// <summary>
    /// Runs a single-shot prompt - the shape teaching (section 13) and recap (section 14) use.
    /// </summary>
    public static Task<ModelCompletion> CompleteAsync(
        this IModelClient client,
        ModelIdentifier model,
        string? systemPrompt,
        string userPrompt,
        ModelCredential credential,
        int maxOutputTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.CompleteAsync(
            ModelRequest.SingleShot(model, systemPrompt, userPrompt, maxOutputTokens),
            credential,
            cancellationToken);
    }

    /// <summary>
    /// Consumes a stream and returns only the text deltas, for callers that want to render tokens
    /// and read usage from a separately captured completion.
    /// </summary>
    public static async IAsyncEnumerable<string> TextDeltasAsync(
        this IAsyncEnumerable<ModelStreamEvent> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await foreach (var @event in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (@event is ModelStreamEvent.TextDelta delta)
            {
                yield return delta.Text;
            }
        }
    }

    /// <summary>Drains a stream and returns its terminal completion.</summary>
    /// <exception cref="ModelClientException">The stream ended without a completion event.</exception>
    public static async Task<ModelCompletion> DrainAsync(
        this IAsyncEnumerable<ModelStreamEvent> stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ModelCompletion? completion = null;
        await foreach (var @event in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (@event is ModelStreamEvent.Completed completed)
            {
                completion = completed.Completion;
            }
        }

        return completion
            ?? throw new ModelClientException("The model stream ended without a completion event.");
    }
}
