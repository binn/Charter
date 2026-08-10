using Charter.Models;

namespace Charter.Tests;

/// <summary>
/// The Phase 1 model defaults (section 4.2, section 20b, change spec 001).
/// </summary>
/// <remarks>
/// One implementation reaches every provider worth having in Phase 1:
/// <see cref="OpenAiCompatibleModelClient"/> pointed at OpenRouter. That is a smaller Phase 1 than a
/// native first-party client, not a larger one - but it only covers the surface Charter itself calls.
/// These tests pin the split, because collapsing it is the failure this design is arranged to avoid.
/// </remarks>
public class ModelDefaultsTests
{
    [Fact]
    public void ControlPlaneModelsDefaultToOpenRouter()
    {
        var options = new ModelClientOptions();

        Assert.Equal(ModelProvider.OpenRouter, options.RefineModel.Provider);
        Assert.Equal(ModelProvider.OpenRouter, options.TeachModel.Provider);
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", options.RefineModel.Canonical);
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", options.TeachModel.Canonical);
    }

    [Fact]
    public void TheBuildModelDoesNotDefaultToOpenRouterBecauseTheAdapterBoundsIt()
    {
        // Section 20b's opening line: Charter consumes models on two surfaces and conflating them is
        // the main hazard. This one is dispatched to an agent CLI, and Claude Code cannot present an
        // OpenRouter credential. Defaulting it to an aggregator would ship a pairing the compatibility
        // resolver is obliged to refuse.
        var options = new ModelClientOptions();

        Assert.Equal(ModelProvider.Anthropic, options.BuildModel.Provider);
        Assert.Equal("claude-opus-5", options.BuildModel.Model);
        Assert.False(options.BuildModel.WasQualified);
    }

    [Fact]
    public void EveryDefaultIdentifierRoundTripsThroughTheParser()
    {
        foreach (var model in new[]
                 {
                     ModelClientOptions.DefaultRefineModel,
                     ModelClientOptions.DefaultBuildModel,
                     ModelClientOptions.DefaultTeachModel,
                 })
        {
            var reparsed = ModelIdentifier.Parse(model.Canonical);

            Assert.Equal(model.Provider, reparsed.Provider);
            Assert.Equal(model.Model, reparsed.Model);
        }
    }

    [Fact]
    public void TheControlPlaneDefaultKeepsItsNestedVendorSegment()
    {
        // openrouter/anthropic/claude-sonnet-5 is a three-segment identifier and only the first is the
        // provider. Splitting on every slash would send `anthropic` to OpenRouter as a model name.
        var refine = new ModelClientOptions().RefineModel;

        Assert.Equal("anthropic/claude-sonnet-5", refine.Model);
    }

    [Fact]
    public void TheOpenRouterDefaultIsRoutedByTheOpenAiCompatibleClient()
    {
        // The change spec's claim, asserted rather than assumed: no new client is needed for Phase 1.
        var client = new OpenAiCompatibleModelClient(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            new ModelClientOptions(),
            ModelTestFixtures.Calculator(),
            TimeProvider.System,
            ModelTestFixtures.Silent<OpenAiCompatibleModelClient>());

        Assert.True(client.Supports(ModelClientOptions.DefaultRefineModel));
        Assert.Contains(ModelProvider.OpenRouter, client.SupportedProviders);
    }

    [Fact]
    public void TheOtherTwoClientsStayRegisteredBecauseThisIsADefaultChangeNotADeletion()
    {
        var factory = new ModelClientFactory(
        [
            new AnthropicModelClient(
                new StubHttpClientFactory(new StubHttpMessageHandler()),
                new ModelClientOptions(),
                ModelTestFixtures.Calculator(),
                TimeProvider.System,
                ModelTestFixtures.Silent<AnthropicModelClient>()),
            new OpenAiCompatibleModelClient(
                new StubHttpClientFactory(new StubHttpMessageHandler()),
                new ModelClientOptions(),
                ModelTestFixtures.Calculator(),
                TimeProvider.System,
                ModelTestFixtures.Silent<OpenAiCompatibleModelClient>()),
            new GeminiModelClient(
                new StubHttpClientFactory(new StubHttpMessageHandler()),
                new ModelClientOptions(),
                ModelTestFixtures.Calculator(),
                TimeProvider.System,
                ModelTestFixtures.Silent<GeminiModelClient>()),
        ]);

        Assert.IsType<OpenAiCompatibleModelClient>(factory.GetClient(ModelClientOptions.DefaultRefineModel));
        Assert.IsType<AnthropicModelClient>(factory.GetClient(ModelClientOptions.DefaultBuildModel));
        Assert.IsType<GeminiModelClient>(factory.GetClient(ModelIdentifier.Parse("google/gemini-2.5-pro")));
    }

    [Fact]
    public void TheShippedTablePricesTheDefaultsEvenWithNoCatalogFetch()
    {
        // A budget estimate has to exist before the first OpenRouter call, and the routed default
        // falls back to its upstream vendor's published price.
        Assert.NotNull(StaticModelPriceTable.TryGetPrice(ModelClientOptions.DefaultRefineModel));
        Assert.NotNull(StaticModelPriceTable.TryGetPrice(ModelClientOptions.DefaultBuildModel));
        Assert.NotNull(StaticModelPriceTable.TryGetPrice(ModelClientOptions.DefaultTeachModel));
    }
}
