using Charter.Configuration;
using Charter.Hosting;
using Charter.Models;
using Charter.Recaps;
using Charter.Refinement;
using Charter.Teaching;
using Microsoft.Extensions.DependencyInjection;
using ConfigModelIdentifier = Charter.Configuration.ModelIdentifier;
using ModelIdentifier = Charter.Models.ModelIdentifier;

namespace Charter.Tests;

/// <summary>
/// Section 4.2 and section 20b: <c>CHARTER_MODEL_REFINE</c> and <c>CHARTER_MODEL_TEACH</c> reach the
/// model client, and <c>CHARTER_MODEL_BUILD</c> stays on the other surface.
/// </summary>
/// <remarks>
/// These assert against the graph <c>CharterHost.ConfigureServices</c> composes, not one the test
/// assembles. That is the whole point: refinement, teaching and recap each registered their own
/// options with a hardcoded <c>claude-sonnet-4-6</c> and a comment saying a host could register one
/// first and win, and no test could see that no host did. A unit test that constructs
/// <c>SpecRefiner</c> by hand passes either way.
/// </remarks>
public class HostModelSelectionTests
{
    private const string Refine = "openrouter/anthropic/claude-sonnet-5";
    private const string Teach = "openrouter/deepseek/deepseek-r1";

    /// <summary>Records which model the refiner asked the factory for, and answers with a stub.</summary>
    private sealed class RecordingClientFactory(IModelClient client) : IModelClientFactory
    {
        public List<ModelIdentifier> Requested { get; } = [];

        public IModelClient GetClient(ModelIdentifier model)
        {
            Requested.Add(model);
            return client;
        }

        public IModelClient GetClient(ModelProvider provider) => client;

        public IModelClient? Find(ModelProvider provider) => client;
    }

    private static ServiceCollection Compose(params (string Key, string? Value)[] overrides)
    {
        var read = ConfigTestEnvironment.With(overrides);
        var config = CharterConfigParser.Parse(read).ConfigOrThrow();

        var services = new ServiceCollection();
        services.AddLogging();
        CharterHost.ConfigureServices(services, config, config.ToStartupOptions(), read);

        return services;
    }

    [Fact]
    public async Task TheConfiguredRefineModelIsWhatTheRefinerAsksTheModelClientFor()
    {
        var services = Compose(("CHARTER_MODEL_REFINE", Refine));

        var factory = new RecordingClientFactory(
            RefinementStubs.Returning(RefinementStubs.GoodSpec));

        // Last registration wins over the TryAdd inside AddCharterModels, so everything else in the
        // graph - including the RefinementOptions the host projected - is exactly what production has.
        services.AddSingleton<IModelClientFactory>(factory);

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var refiner = scope.ServiceProvider.GetRequiredService<ISpecRefiner>();

        var result = await refiner.AdvanceAsync(
            RefinementConversation.Start(InteractionMode.Plan),
            RequesterText.From("show the derate percentage on each quote line"),
            RefinementStubs.Context(),
            new ModelCredential
            {
                Id = "grant-1",
                Kind = ModelCredentialKind.OpenRouterKey,
                Secret = new ModelSecret("sk-or-not-a-real-key"),
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);

        var asked = Assert.Single(factory.Requested);
        Assert.Equal(Refine, asked.Canonical);

        // The specific regression: the hardcoded default sent an OpenRouter key to Anthropic, which
        // answers 401 and gets the grant marked invalid for a fault that was Charter's.
        Assert.Equal(ModelProvider.OpenRouter, asked.Provider);
        Assert.NotEqual(ModelProvider.Anthropic, asked.Provider);
    }

    [Fact]
    public void RefinementTeachingAndRecapAllRunOnTheConfiguredControlPlaneModels()
    {
        var services = Compose(
            ("CHARTER_MODEL_REFINE", Refine),
            ("CHARTER_MODEL_TEACH", Teach));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(Refine, provider.GetRequiredService<RefinementOptions>().Model.Canonical);
        Assert.Equal(Teach, provider.GetRequiredService<TeachingOptions>().Model.Canonical);

        // Section 4.2 gives the recap no variable of its own; it is a control-plane summary of a
        // finished session, which is what teaching is, so it follows CHARTER_MODEL_TEACH rather than
        // a name no operator can move.
        Assert.Equal(Teach, provider.GetRequiredService<RecapOptions>().Model.Canonical);
    }

    [Fact]
    public void NoControlPlaneSubsystemIsLeftOnTheHardcodedDefault()
    {
        // The defect was not "the wrong default", it was "a default that no variable could move".
        // Every one of these was claude-sonnet-4-6 on Anthropic regardless of configuration.
        var services = Compose(
            ("CHARTER_MODEL_REFINE", "openrouter/qwen/qwen3-max"),
            ("CHARTER_MODEL_TEACH", "google/gemini-3-pro"));

        using var provider = services.BuildServiceProvider();

        var models = new[]
        {
            provider.GetRequiredService<RefinementOptions>().Model,
            provider.GetRequiredService<TeachingOptions>().Model,
            provider.GetRequiredService<RecapOptions>().Model,
        };

        Assert.DoesNotContain(models, model => model.Model.Contains("sonnet-4-6", StringComparison.Ordinal));
        Assert.Equal("openrouter/qwen/qwen3-max", models[0].Canonical);
        Assert.Equal(ModelProvider.Google, models[1].Provider);
    }

    [Fact]
    public void TheBuildModelStaysOnTheAgentSurfaceAndKeepsItsBareAnthropicDefault()
    {
        // Section 20b's opening distinction, and the trap in this change. CHARTER_MODEL_BUILD is not
        // a call Charter makes: it is a string handed to an agent CLI, bounded by what that CLI can
        // authenticate against. Defaulting it to OpenRouter to "match" the other two would ship a
        // pairing section 12b obliges the UI to refuse.
        using var provider = Compose().BuildServiceProvider();

        var models = provider.GetRequiredService<ModelConfig>();

        Assert.Equal(ModelProvider.Anthropic, ModelIdentifier.Parse(models.Build.Qualified).Provider);
        Assert.Equal("anthropic/claude-opus-5", models.Build.Qualified);

        // ...while the two control-plane defaults are OpenRouter-qualified, as section 4.2 writes them.
        Assert.Equal("openrouter", models.Refine.Provider);
        Assert.Equal("openrouter", models.Teach.Provider);
    }

    [Fact]
    public void EveryProviderTheConfigParserAcceptsIsOneTheModelLayerCanRoute()
    {
        // The two ModelIdentifier types are deliberately separate - one parses text and explains
        // faults, the other picks a transport - so the bridge between them has to be total. A
        // provider the parser accepts and the model layer rejects would fail at the first refinement
        // rather than at startup, which is the failure mode section 4.1 exists to prevent.
        foreach (var provider in ConfigModelIdentifier.KnownProviders)
        {
            var qualified = $"{provider}/some-model";

            Assert.True(
                ConfigModelIdentifier.TryParse(qualified, out var parsed, out _),
                $"the config parser rejected {qualified}");

            Assert.True(
                ModelIdentifier.TryParse(parsed!.Qualified, out _),
                $"the model layer cannot route '{parsed.Qualified}', which the config parser accepts");
        }
    }
}
