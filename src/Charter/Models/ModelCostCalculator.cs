namespace Charter.Models;

/// <summary>Turns token counts into a ledger entry. Sections 20b.5 and 34.4.</summary>
public interface IModelCostCalculator
{
    /// <summary>Prices one invocation against the credential that served it.</summary>
    ValueTask<ModelCharge> CalculateAsync(
        ModelIdentifier model,
        ModelUsage usage,
        ModelCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Prices an invocation, preferring what the provider reported over what a price table predicts, and
/// charging subscription grants in quota rather than dollars.
/// </summary>
public sealed class ModelCostCalculator : IModelCostCalculator
{
    private readonly IModelPriceCatalog _catalog;

    /// <summary>Creates a calculator.</summary>
    public ModelCostCalculator(IModelPriceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public async ValueTask<ModelCharge> CalculateAsync(
        ModelIdentifier model,
        ModelUsage usage,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(credential);

        decimal notional;
        ModelCostBasis basis;

        if (usage.ProviderReportedCostUsd is { } reported)
        {
            // Section 20b.6: where a provider reports cost directly, that beats an estimate.
            notional = reported;
            basis = ModelCostBasis.ProviderReported;
        }
        else
        {
            var price = await _catalog.TryGetPriceAsync(model, cancellationToken).ConfigureAwait(false);
            if (price is { } known)
            {
                notional = Estimate(usage, known);
                basis = ModelCostBasis.Estimated;
            }
            else
            {
                notional = 0m;
                basis = ModelCostBasis.Unpriced;
            }
        }

        // Section 20b.5: a subscription-backed call has no marginal cost but consumes scarce quota,
        // so it is charged in quota and the dollar figure is kept only as a notional comparison.
        var isSubscription = credential.IsSubscription;

        return new ModelCharge
        {
            Unit = isSubscription ? ModelChargeUnit.SubscriptionQuota : ModelChargeUnit.Usd,
            CostUsd = isSubscription ? 0m : notional,
            NotionalCostUsd = notional,
            Basis = basis,
            CredentialId = credential.Id,
            OwnerUserId = credential.OwnerUserId,
        };
    }

    /// <summary>Estimates the dollar cost of a usage record at a given price.</summary>
    public static decimal Estimate(ModelUsage usage, ModelPrice price)
    {
        ArgumentNullException.ThrowIfNull(usage);

        const decimal Million = 1_000_000m;
        var cost =
            (usage.InputTokens * price.InputPerMillion / Million)
            + (usage.OutputTokens * price.OutputPerMillion / Million)
            + (usage.CacheReadInputTokens * price.EffectiveCacheReadPerMillion / Million)
            + (usage.CacheWriteInputTokens * price.EffectiveCacheWritePerMillion / Million);

        return decimal.Round(cost, 8, MidpointRounding.AwayFromZero);
    }
}
