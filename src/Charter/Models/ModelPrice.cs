namespace Charter.Models;

/// <summary>Per-token prices for one model, in USD per million tokens.</summary>
/// <param name="InputPerMillion">Uncached prompt tokens.</param>
/// <param name="OutputPerMillion">Generated tokens.</param>
/// <param name="CacheReadPerMillion">Prompt tokens served from a provider cache.</param>
/// <param name="CacheWritePerMillion">Prompt tokens written to a provider cache.</param>
public readonly record struct ModelPrice(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null)
{
    /// <summary>
    /// Cache reads bill at roughly a tenth of the input rate where a provider does not publish a
    /// separate figure.
    /// </summary>
    public decimal EffectiveCacheReadPerMillion => CacheReadPerMillion ?? (InputPerMillion * 0.1m);

    /// <summary>
    /// Cache writes bill at roughly 1.25x the input rate for the short-lived tier where a provider
    /// does not publish a separate figure.
    /// </summary>
    public decimal EffectiveCacheWritePerMillion => CacheWritePerMillion ?? (InputPerMillion * 1.25m);
}

/// <summary>A source of per-token prices.</summary>
public interface IModelPriceCatalog
{
    /// <summary>Looks up the price for a model, if this catalog knows it.</summary>
    ValueTask<ModelPrice?> TryGetPriceAsync(
        ModelIdentifier model,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Prices for the models Charter ships defaults for. Seed data, not the last word: section 20b.6 is
/// explicit that a hardcoded table cannot serve a budget estimator when the model is user-selectable,
/// which is why <see cref="OpenRouterModelCatalog"/> exists and is consulted first for OpenRouter.
/// </summary>
public sealed class StaticModelPriceTable : IModelPriceCatalog
{
    private static readonly Dictionary<string, ModelPrice> Prices = BuildPrices();

    /// <inheritdoc />
    public ValueTask<ModelPrice?> TryGetPriceAsync(
        ModelIdentifier model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryGetPrice(model));
    }

    /// <summary>Looks up the price for a model.</summary>
    public static ModelPrice? TryGetPrice(ModelIdentifier model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Prices.TryGetValue(model.Canonical, out var exact))
        {
            return exact;
        }

        // An OpenRouter-routed model names its upstream vendor, e.g. openrouter/anthropic/claude-opus-5.
        // Fall back to the upstream price when the live catalog has not been fetched.
        if (model.Provider == ModelProvider.OpenRouter
            && ModelIdentifier.TryParse(model.Model, out var upstream)
            && upstream.WasQualified
            && Prices.TryGetValue(upstream.Canonical, out var routed))
        {
            return routed;
        }

        return null;
    }

    /// <summary>Every model this table knows a price for.</summary>
    public static IReadOnlyCollection<string> KnownModels => Prices.Keys;

    private static Dictionary<string, ModelPrice> BuildPrices()
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

        void Add(ModelProvider provider, string model, decimal input, decimal output)
        {
            prices[ModelIdentifier.Create(provider, model).Canonical] = new ModelPrice(input, output);
        }

        // Anthropic, USD per million tokens.
        Add(ModelProvider.Anthropic, "claude-opus-5", 5.00m, 25.00m);
        Add(ModelProvider.Anthropic, "claude-opus-4-8", 5.00m, 25.00m);
        Add(ModelProvider.Anthropic, "claude-opus-4-7", 5.00m, 25.00m);
        Add(ModelProvider.Anthropic, "claude-opus-4-6", 5.00m, 25.00m);
        Add(ModelProvider.Anthropic, "claude-sonnet-5", 3.00m, 15.00m);
        Add(ModelProvider.Anthropic, "claude-sonnet-4-6", 3.00m, 15.00m);
        Add(ModelProvider.Anthropic, "claude-haiku-4-5", 1.00m, 5.00m);

        // OpenAI.
        Add(ModelProvider.OpenAi, "gpt-5", 1.25m, 10.00m);
        Add(ModelProvider.OpenAi, "gpt-5-mini", 0.25m, 2.00m);
        Add(ModelProvider.OpenAi, "gpt-4.1", 2.00m, 8.00m);
        Add(ModelProvider.OpenAi, "gpt-4.1-mini", 0.40m, 1.60m);
        Add(ModelProvider.OpenAi, "o4-mini", 1.10m, 4.40m);

        // Google Gemini.
        Add(ModelProvider.Google, "gemini-2.5-pro", 1.25m, 10.00m);
        Add(ModelProvider.Google, "gemini-2.5-flash", 0.30m, 2.50m);

        // Self-hosted endpoints have no per-token price. Left absent deliberately: an absent price
        // reports as Unpriced rather than as free.
        return prices;
    }
}

/// <summary>
/// Consults each catalog in turn and returns the first price found. Registered with the live
/// OpenRouter catalog ahead of the static table so a user-selectable model is priced from the
/// provider's own published figures.
/// </summary>
public sealed class CompositeModelPriceCatalog : IModelPriceCatalog
{
    private readonly IReadOnlyList<IModelPriceCatalog> _catalogs;

    /// <summary>Creates a composite catalog.</summary>
    public CompositeModelPriceCatalog(IEnumerable<IModelPriceCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _catalogs = catalogs.ToList();
    }

    /// <inheritdoc />
    public async ValueTask<ModelPrice?> TryGetPriceAsync(
        ModelIdentifier model,
        CancellationToken cancellationToken = default)
    {
        foreach (var catalog in _catalogs)
        {
            var price = await catalog.TryGetPriceAsync(model, cancellationToken).ConfigureAwait(false);
            if (price is not null)
            {
                return price;
            }
        }

        return null;
    }
}
