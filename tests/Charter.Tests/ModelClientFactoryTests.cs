using Charter.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>Section 20b.1: three implementations cover every required provider.</summary>
public class ModelClientFactoryTests
{
    [Fact]
    public void EveryProviderResolvesToAClient()
    {
        var factory = BuildFactory();

        foreach (var provider in Enum.GetValues<ModelProvider>())
        {
            Assert.NotNull(factory.Find(provider));
        }
    }

    [Theory]
    [InlineData("claude-opus-5", typeof(AnthropicModelClient))]
    [InlineData("anthropic/claude-sonnet-5", typeof(AnthropicModelClient))]
    [InlineData("openrouter/deepseek/deepseek-r1", typeof(OpenAiCompatibleModelClient))]
    [InlineData("openai/gpt-5", typeof(OpenAiCompatibleModelClient))]
    [InlineData("xai/grok-4", typeof(OpenAiCompatibleModelClient))]
    [InlineData("ollama/qwen2.5-coder", typeof(OpenAiCompatibleModelClient))]
    [InlineData("azure/my-deployment", typeof(OpenAiCompatibleModelClient))]
    [InlineData("google/gemini-2.5-pro", typeof(GeminiModelClient))]
    public void IdentifiersRouteToTheRightTransport(string identifier, Type expected)
    {
        var factory = BuildFactory();

        var client = factory.GetClient(ModelIdentifier.Parse(identifier));

        Assert.IsType(expected, client);
        Assert.True(client.Supports(ModelIdentifier.Parse(identifier)));
    }

    [Fact]
    public void TwoClientsClaimingTheSameProviderIsRejected()
    {
        var one = new AnthropicModelClient(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            new ModelClientOptions(),
            ModelTestFixtures.Calculator(),
            TimeProvider.System,
            ModelTestFixtures.Silent<AnthropicModelClient>());

        var two = new AnthropicModelClient(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            new ModelClientOptions(),
            ModelTestFixtures.Calculator(),
            TimeProvider.System,
            ModelTestFixtures.Silent<AnthropicModelClient>());

        Assert.Throws<ArgumentException>(() => new ModelClientFactory([one, two]));
    }

    [Fact]
    public void AddCharterModelsWiresTheGraphWithoutTheHostBindingCredentialStorage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterModels();

        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IModelClientFactory>();
        Assert.IsType<AnthropicModelClient>(factory.GetClient(ModelProvider.Anthropic));
        Assert.IsType<GeminiModelClient>(factory.GetClient(ModelProvider.Google));
        Assert.IsType<OpenAiCompatibleModelClient>(factory.GetClient(ModelProvider.OpenRouter));

        Assert.NotNull(provider.GetRequiredService<IModelCostCalculator>());
        Assert.NotNull(provider.GetRequiredService<IModelPriceCatalog>());

        // The credential store belongs to the persistence layer, so it is deliberately unbound here.
        Assert.Null(provider.GetService<IModelCredentialStore>());
    }

    [Fact]
    public void AHostSuppliedOptionsRecordWins()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ModelClientOptions
        {
            RefineModel = ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1"),
        });
        services.AddCharterModels();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "openrouter/deepseek/deepseek-r1",
            provider.GetRequiredService<ModelClientOptions>().RefineModel.Canonical);
    }

    [Fact]
    public void DefaultOptionsMatchSection42()
    {
        var options = new ModelClientOptions();

        // Two surfaces, two defaults: Charter calls the first and third itself and reaches them
        // through OpenRouter; the second is handed to an agent CLI. See ModelDefaultsTests.
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", options.RefineModel.Canonical);
        Assert.Equal("anthropic/claude-opus-5", options.BuildModel.Canonical);
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", options.TeachModel.Canonical);
        Assert.Equal(ModelFailoverPolicy.PauseAndResume, options.FailoverPolicy);
    }

    [Fact]
    public void ACredentialBaseUrlOverridesTheProviderDefault()
    {
        var options = new ModelClientOptions();
        var credential = ModelTestFixtures.ApiKey(
            "k",
            ModelCredentialKind.CustomOpenAiCompatible,
            baseUrl: new Uri("https://gateway.internal/v1"));

        Assert.Equal(
            new Uri("https://gateway.internal/v1"),
            options.ResolveBaseUrl(credential, ModelProvider.OpenAiCompatible));
    }

    private static ModelClientFactory BuildFactory()
    {
        var httpClientFactory = new StubHttpClientFactory(new StubHttpMessageHandler());
        var options = new ModelClientOptions();
        var calculator = ModelTestFixtures.Calculator();

        return new ModelClientFactory(
        [
            new AnthropicModelClient(
                httpClientFactory,
                options,
                calculator,
                TimeProvider.System,
                ModelTestFixtures.Silent<AnthropicModelClient>()),
            new OpenAiCompatibleModelClient(
                httpClientFactory,
                options,
                calculator,
                TimeProvider.System,
                ModelTestFixtures.Silent<OpenAiCompatibleModelClient>()),
            new GeminiModelClient(
                httpClientFactory,
                options,
                calculator,
                TimeProvider.System,
                ModelTestFixtures.Silent<GeminiModelClient>()),
        ]);
    }
}
