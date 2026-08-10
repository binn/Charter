using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>Sections 20b.5 and 20b.6: cost accounting and the live OpenRouter catalog.</summary>
public class ModelCostTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EstimateMultipliesTokensByThePerMillionRate()
    {
        var usage = new ModelUsage { InputTokens = 1_000_000, OutputTokens = 500_000 };
        var price = new ModelPrice(5.00m, 25.00m);

        // 1M input at $5 plus 0.5M output at $25.
        Assert.Equal(17.50m, ModelCostCalculator.Estimate(usage, price));
    }

    [Fact]
    public void CachedTokensAreBilledAtTheirOwnRates()
    {
        var usage = new ModelUsage
        {
            InputTokens = 0,
            OutputTokens = 0,
            CacheReadInputTokens = 1_000_000,
            CacheWriteInputTokens = 1_000_000,
        };

        // Defaults: reads at 0.1x input, writes at 1.25x input.
        Assert.Equal(6.75m, ModelCostCalculator.Estimate(usage, new ModelPrice(5.00m, 25.00m)));
    }

    [Fact]
    public async Task AMeteredGrantIsChargedInDollars()
    {
        var calculator = ModelTestFixtures.Calculator();
        var usage = new ModelUsage { InputTokens = 200_000, OutputTokens = 100_000 };

        var charge = await calculator.CalculateAsync(
            ModelIdentifier.Parse("claude-opus-5"),
            usage,
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelChargeUnit.Usd, charge.Unit);
        Assert.Equal(ModelCostBasis.Estimated, charge.Basis);
        Assert.Equal(3.50m, charge.CostUsd);
        Assert.Equal(3.50m, charge.NotionalCostUsd);
    }

    [Fact]
    public async Task ASubscriptionGrantIsChargedInQuotaButStillReportsANotionalCost()
    {
        var calculator = ModelTestFixtures.Calculator();
        var usage = new ModelUsage { InputTokens = 200_000, OutputTokens = 100_000 };

        var credential = ModelTestFixtures.ApiKey("sub", ModelCredentialKind.AnthropicOAuth) with
        {
            OwnerUserId = "user-7",
        };

        var charge = await calculator.CalculateAsync(
            ModelIdentifier.Parse("claude-opus-5"),
            usage,
            credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelChargeUnit.SubscriptionQuota, charge.Unit);
        Assert.Equal(0m, charge.CostUsd);

        // Section 20b.5: reporting a subscription session as $0.00 makes budget dashboards lie, so
        // the same tokens are also priced notionally and attributed to the owner.
        Assert.Equal(3.50m, charge.NotionalCostUsd);
        Assert.Equal("user-7", charge.OwnerUserId);
        Assert.Equal("sub", charge.CredentialId);
    }

    [Fact]
    public async Task AProviderReportedCostWinsOverThePriceTable()
    {
        var calculator = ModelTestFixtures.Calculator();
        var usage = new ModelUsage
        {
            InputTokens = 200_000,
            OutputTokens = 100_000,
            ProviderReportedCostUsd = 0.123456m,
        };

        var charge = await calculator.CalculateAsync(
            ModelIdentifier.Parse("claude-opus-5"),
            usage,
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.AnthropicApiKey),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCostBasis.ProviderReported, charge.Basis);
        Assert.Equal(0.123456m, charge.CostUsd);
    }

    [Fact]
    public async Task AnUnknownModelReportsUnpricedRatherThanFree()
    {
        var calculator = ModelTestFixtures.Calculator();

        var charge = await calculator.CalculateAsync(
            ModelIdentifier.Parse("ollama/qwen2.5-coder"),
            new ModelUsage { InputTokens = 5_000, OutputTokens = 5_000 },
            ModelTestFixtures.ApiKey("k", ModelCredentialKind.CustomOpenAiCompatible),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCostBasis.Unpriced, charge.Basis);
        Assert.Equal(0m, charge.CostUsd);
    }

    [Fact]
    public void TheShippedTableKnowsTheSection42Defaults()
    {
        Assert.NotNull(StaticModelPriceTable.TryGetPrice(ModelIdentifier.Parse("claude-sonnet-5")));
        Assert.NotNull(StaticModelPriceTable.TryGetPrice(ModelIdentifier.Parse("claude-opus-5")));
    }

    [Fact]
    public void AnOpenRouterRoutedModelFallsBackToItsUpstreamPrice()
    {
        var price = StaticModelPriceTable.TryGetPrice(
            ModelIdentifier.Parse("openrouter/anthropic/claude-opus-5"));

        Assert.NotNull(price);
        Assert.Equal(5.00m, price.Value.InputPerMillion);
    }

    [Fact]
    public async Task TheOpenRouterCatalogConvertsPerTokenPricesToPerMillionAndCachesThem()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """
            {"data":[
              {"id":"deepseek/deepseek-r1","name":"DeepSeek R1","context_length":163840,
               "pricing":{"prompt":"0.0000004","completion":"0.0000016"}},
              {"id":"anthropic/claude-opus-5","name":"Claude Opus 5",
               "pricing":{"prompt":"0.000005","completion":"0.000025"}}
            ]}
            """);

        var time = new ModelFakeTimeProvider(Now);
        var catalog = new OpenRouterModelCatalog(
            new StubHttpClientFactory(handler).CreateClient("x"),
            new ModelClientOptions(),
            time,
            NullLogger<OpenRouterModelCatalog>.Instance);

        var price = await catalog.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(price);
        Assert.Equal(0.40m, price.Value.InputPerMillion);
        Assert.Equal(1.60m, price.Value.OutputPerMillion);
        Assert.Equal(2, catalog.Count);

        // Inside the TTL a second lookup must not refetch: the stub would throw if it did.
        var again = await catalog.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/anthropic/claude-opus-5"),
            TestContext.Current.CancellationToken);

        Assert.Equal(5.00m, again!.Value.InputPerMillion);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TheCatalogRefetchesOnceItsTtlHasPassed()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"data":[{"id":"a/b","pricing":{"prompt":"0.000001","completion":"0.000002"}}]}""")
            .EnqueueJson("""{"data":[{"id":"a/b","pricing":{"prompt":"0.000003","completion":"0.000004"}}]}""");

        var time = new ModelFakeTimeProvider(Now);
        var options = new ModelClientOptions { OpenRouterCatalogTtl = TimeSpan.FromMinutes(30) };
        var catalog = new OpenRouterModelCatalog(
            new StubHttpClientFactory(handler).CreateClient("x"),
            options,
            time,
            NullLogger<OpenRouterModelCatalog>.Instance);

        var first = await catalog.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/a/b"),
            TestContext.Current.CancellationToken);
        Assert.Equal(1.00m, first!.Value.InputPerMillion);

        time.Now = Now.AddHours(1);

        var second = await catalog.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/a/b"),
            TestContext.Current.CancellationToken);
        Assert.Equal(3.00m, second!.Value.InputPerMillion);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AFailedCatalogFetchDegradesToTheShippedTableRatherThanThrowing()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => throw new HttpRequestException("network is down"));

        var catalog = new OpenRouterModelCatalog(
            new StubHttpClientFactory(handler).CreateClient("x"),
            new ModelClientOptions(),
            new ModelFakeTimeProvider(Now),
            NullLogger<OpenRouterModelCatalog>.Instance);

        var composite = new CompositeModelPriceCatalog([catalog, new StaticModelPriceTable()]);

        var price = await composite.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/anthropic/claude-opus-5"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(price);
        Assert.Equal(5.00m, price.Value.InputPerMillion);
    }

    [Fact]
    public async Task TheCompositeCatalogPrefersTheLiveOpenRouterPrice()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(
            """{"data":[{"id":"anthropic/claude-opus-5","pricing":{"prompt":"0.000009","completion":"0.000045"}}]}""");

        var catalog = new OpenRouterModelCatalog(
            new StubHttpClientFactory(handler).CreateClient("x"),
            new ModelClientOptions(),
            new ModelFakeTimeProvider(Now),
            NullLogger<OpenRouterModelCatalog>.Instance);

        var composite = new CompositeModelPriceCatalog([catalog, new StaticModelPriceTable()]);

        var price = await composite.TryGetPriceAsync(
            ModelIdentifier.Parse("openrouter/anthropic/claude-opus-5"),
            TestContext.Current.CancellationToken);

        Assert.Equal(9.00m, price!.Value.InputPerMillion);
    }
}
