using Charter.Adapters;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the adapter catalog and compatibility resolver (section 12b).
/// </summary>
/// <remarks>
/// Placed in <c>Microsoft.Extensions.DependencyInjection</c> so <c>AddCharterAdapters()</c> resolves
/// from <c>Program.cs</c> with no extra using directive, matching the convention of every other
/// <c>Add*</c> in the file.
/// </remarks>
public static class AdapterServiceCollectionExtensions
{
    /// <summary>
    /// Loads every adapter from the in-tree <c>adapters/</c> directory and from
    /// <c>CHARTER_ADAPTERS_PATH</c>, and registers the result.
    /// </summary>
    /// <remarks>
    /// Loading happens here rather than lazily on first use. An unreadable adapter file is a startup
    /// problem and should surface with everything else at boot, not when a requester files their
    /// first request (AGENTS.md, section 4.1).
    /// </remarks>
    public static IServiceCollection AddCharterAdapters(this IServiceCollection services)
        => services.AddCharterAdapters(AdapterSources.FromEnvironment());

    /// <summary>Registers the catalog from explicit source directories.</summary>
    public static IServiceCollection AddCharterAdapters(this IServiceCollection services, AdapterSources sources)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sources);

        return services.AddCharterAdapters(AdapterCatalog.Load(sources));
    }

    /// <summary>Registers an already-loaded catalog.</summary>
    public static IServiceCollection AddCharterAdapters(this IServiceCollection services, IAdapterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        services.AddSingleton(catalog);
        services.AddSingleton(new AdapterCompatibilityResolver());
        services.AddHostedService(provider => new AdapterCatalogReport(
            provider.GetRequiredService<IAdapterCatalog>(),
            provider.GetRequiredService<ILogger<AdapterCatalogReport>>()));

        return services;
    }
}
