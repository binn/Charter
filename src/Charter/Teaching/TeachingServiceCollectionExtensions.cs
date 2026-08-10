using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Teaching;

/// <summary>Registers teaching (sections 13, 34.6).</summary>
public static class TeachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the teaching generator, the prompt builder, the teaching options, and process-local
    /// defaults for the three stores teaching needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <em>after</em> <c>AddCharterModels()</c>: the generator resolves
    /// <c>IModelClientFactory</c> from there.
    /// </para>
    /// <para>
    /// <see cref="IConceptLedgerStore"/>, <see cref="IWalkthroughStore"/> and
    /// <see cref="IExplainThisQuota"/> are registered with in-memory implementations through
    /// <c>TryAdd</c>, so a data-layer implementation registered before this call wins and one
    /// registered after it does not need to. The in-memory versions are real, not stubs — a fresh
    /// instance with no persistence still teaches, it just forgets when the container restarts.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterTeaching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new TeachingOptions());
        services.TryAddSingleton<TeachingPromptBuilder>();
        services.TryAddSingleton<IConceptLedgerStore, InMemoryConceptLedgerStore>();
        services.TryAddSingleton<IWalkthroughStore, InMemoryWalkthroughStore>();
        services.TryAddSingleton<IExplainThisQuota, InMemoryExplainThisQuota>();
        services.TryAddScoped<ITeachingGenerator, TeachingGenerator>();

        return services;
    }
}
