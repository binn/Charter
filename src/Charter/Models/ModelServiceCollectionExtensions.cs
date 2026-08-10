using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Models;

/// <summary>Registers the control-plane model abstraction (section 20b).</summary>
public static class ModelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the three <see cref="IModelClient"/> implementations, the client factory, the
    /// pricing catalogs, the cost calculator and the section 20b.3 credential resolver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two collaborators are deliberately left unregistered because they belong to other layers:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="IModelCredentialStore"/> - the persistence layer owns <c>CredentialGrant</c> and
    /// its encryption, so the host binds it to the EF-backed implementation.
    /// </description></item>
    /// <item><description>
    /// <see cref="ModelClientOptions"/> - registered here with section 4.2 defaults if the host has
    /// not already supplied one, so the host can register its own projection of <c>CharterConfig</c>
    /// before calling this and have it win.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddCharterModels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ModelClientOptions());

        services.AddTransient<ModelHttpDiagnosticsHandler>();

        services.AddHttpClient(AnthropicModelClient.HttpClientName)
            .AddHttpMessageHandler<ModelHttpDiagnosticsHandler>();
        services.AddHttpClient(OpenAiCompatibleModelClient.HttpClientName);
        services.AddHttpClient(GeminiModelClient.HttpClientName);
        services.AddHttpClient(OpenRouterModelCatalog.HttpClientName);

        // Section 20b.6: the live OpenRouter catalog is consulted before the shipped table, because
        // a hardcoded table cannot price a model the user chose after Charter was built.
        services.TryAddSingleton(provider => new OpenRouterModelCatalog(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouterModelCatalog.HttpClientName),
            provider.GetRequiredService<ModelClientOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenRouterModelCatalog>>()));

        services.TryAddSingleton<StaticModelPriceTable>();
        services.TryAddSingleton<IModelPriceCatalog>(provider => new CompositeModelPriceCatalog(
        [
            provider.GetRequiredService<OpenRouterModelCatalog>(),
            provider.GetRequiredService<StaticModelPriceTable>(),
        ]));

        services.TryAddSingleton<IModelCostCalculator, ModelCostCalculator>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModelClient, AnthropicModelClient>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModelClient, OpenAiCompatibleModelClient>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModelClient, GeminiModelClient>());

        services.TryAddSingleton<IModelClientFactory, ModelClientFactory>();
        services.TryAddScoped<ICredentialResolver, CredentialResolver>();

        return services;
    }
}
