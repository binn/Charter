using Charter.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // Section 20b.7: the instance-level pooling switch. Registered with TryAdd and defaulted to
        // off, so a host projecting CHARTER_ALLOW_SHARED_POOL registers first and wins, and a graph
        // that forgets to gets the section 4.2 default rather than an open pool.
        services.TryAddSingleton(CredentialPolicy.Default);

        // Section 19: metadata by default, bodies behind CHARTER_LOG_INCLUDE_TRANSCRIPTS. The host
        // registers the real one from StartupOptions before this runs; the fallback withholds bodies,
        // because the safe position is the one to arrive at by accident.
        services.TryAddSingleton(provider => TranscriptLog.MetadataOnly(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<TranscriptLog>()));

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

        // Every registered client is wrapped, so section 19's rule holds for whichever provider a
        // session resolves to rather than for the ones somebody remembered to instrument.
        services.TryAddSingleton<IModelClientFactory>(provider => new ModelClientFactory(
            provider.GetServices<IModelClient>().Select(client => new TranscriptLoggingModelClient(
                client,
                provider.GetRequiredService<ITranscriptLog>()))));

        services.TryAddScoped<ICredentialResolver>(provider => new CredentialResolver(
            provider.GetRequiredService<IModelCredentialStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<CredentialResolver>>(),
            provider.GetRequiredService<CredentialPolicy>()));

        return services;
    }
}
