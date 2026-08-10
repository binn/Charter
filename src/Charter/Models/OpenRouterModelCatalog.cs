using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>One entry from OpenRouter's model catalog.</summary>
/// <param name="Id">The OpenRouter model id, e.g. <c>deepseek/deepseek-r1</c>.</param>
/// <param name="Name">The display name.</param>
/// <param name="ContextLength">The context window, where OpenRouter publishes one.</param>
/// <param name="Price">Per-token prices, converted to USD per million tokens.</param>
public sealed record OpenRouterModel(string Id, string Name, long? ContextLength, ModelPrice Price);

/// <summary>
/// OpenRouter's live model and pricing catalog, cached.
/// </summary>
/// <remarks>
/// Section 20b.6: the budget estimator cannot work off a hardcoded price table when the model is
/// user-selectable, so the catalog is fetched and cached. A fetch failure is not fatal - a stale
/// entry is served if there is one, and otherwise the caller falls through to the static table and
/// reports the charge as <see cref="ModelCostBasis.Unpriced"/>.
/// </remarks>
public sealed class OpenRouterModelCatalog : IModelPriceCatalog
{
    /// <summary>The named <see cref="HttpClient"/> this catalog resolves.</summary>
    public const string HttpClientName = "charter-openrouter-catalog";

    private readonly HttpClient _httpClient;
    private readonly ModelClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenRouterModelCatalog> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, OpenRouterModel> _models =
        new Dictionary<string, OpenRouterModel>(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>Creates a catalog.</summary>
    public OpenRouterModelCatalog(
        HttpClient httpClient,
        ModelClientOptions options,
        TimeProvider timeProvider,
        ILogger<OpenRouterModelCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Whether the cache is populated and still inside its TTL.</summary>
    public bool IsFresh => _models.Count > 0
        && _timeProvider.GetUtcNow() - _fetchedAt < _options.OpenRouterCatalogTtl;

    /// <summary>The number of cached models.</summary>
    public int Count => _models.Count;

    /// <inheritdoc />
    public async ValueTask<ModelPrice?> TryGetPriceAsync(
        ModelIdentifier model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Provider != ModelProvider.OpenRouter)
        {
            return null;
        }

        var entry = await GetModelAsync(model.Model, cancellationToken).ConfigureAwait(false);
        return entry?.Price;
    }

    /// <summary>Looks up one catalog entry, refreshing the cache first if it has gone stale.</summary>
    public async ValueTask<OpenRouterModel?> GetModelAsync(
        string openRouterModelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openRouterModelId);

        var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.TryGetValue(openRouterModelId, out var entry) ? entry : null;
    }

    /// <summary>Returns the whole catalog, refreshing it first if it has gone stale.</summary>
    public async ValueTask<IReadOnlyDictionary<string, OpenRouterModel>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsFresh)
        {
            return _models;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsFresh)
            {
                return _models;
            }

            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            return _models;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Forces a refetch regardless of TTL.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _httpClient
                .GetFromJsonAsync(_options.OpenRouterModelsEndpoint, OpenRouterJson.Default.OpenRouterCatalogResponse, cancellationToken)
                .ConfigureAwait(false);

            if (payload?.Data is null || payload.Data.Count == 0)
            {
                _logger.LogWarning("The OpenRouter model catalog returned no entries; keeping the previous cache.");
                return;
            }

            var parsed = new Dictionary<string, OpenRouterModel>(payload.Data.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in payload.Data)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                parsed[entry.Id] = new OpenRouterModel(
                    entry.Id,
                    entry.Name ?? entry.Id,
                    entry.ContextLength,
                    ToPrice(entry.Pricing));
            }

            _models = parsed;
            _fetchedAt = _timeProvider.GetUtcNow();
            _logger.LogInformation("Cached {ModelCount} models from the OpenRouter catalog.", parsed.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // A pricing catalog that cannot be fetched must not take the control plane down with it.
            // A stale cache still prices better than nothing, and an empty one degrades to Unpriced.
            _logger.LogWarning(
                ex,
                "Could not refresh the OpenRouter model catalog; {ModelCount} cached entries remain in use.",
                _models.Count);
        }
    }

    /// <summary>
    /// OpenRouter publishes prices as USD per token, as decimal strings. Charter's tables are USD per
    /// million tokens.
    /// </summary>
    private static ModelPrice ToPrice(OpenRouterPricing? pricing)
    {
        if (pricing is null)
        {
            return new ModelPrice(0m, 0m);
        }

        return new ModelPrice(
            PerMillion(pricing.Prompt),
            PerMillion(pricing.Completion),
            pricing.InputCacheRead is null ? null : PerMillion(pricing.InputCacheRead),
            pricing.InputCacheWrite is null ? null : PerMillion(pricing.InputCacheWrite));
    }

    private static decimal PerMillion(string? perToken) =>
        decimal.TryParse(perToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value * 1_000_000m
            : 0m;

}

internal sealed record OpenRouterCatalogResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<OpenRouterCatalogEntry>? Data { get; init; }
}

internal sealed record OpenRouterCatalogEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("context_length")]
    public long? ContextLength { get; init; }

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; init; }
}

internal sealed record OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("completion")]
    public string? Completion { get; init; }

    [JsonPropertyName("input_cache_read")]
    public string? InputCacheRead { get; init; }

    [JsonPropertyName("input_cache_write")]
    public string? InputCacheWrite { get; init; }
}

[JsonSerializable(typeof(OpenRouterCatalogResponse))]
internal sealed partial class OpenRouterJson : JsonSerializerContext;
