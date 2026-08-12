using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Refinement;

/// <summary>Registers spec refinement (sections 10, 10b, 16).</summary>
public static class RefinementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the refiner, the prompt builder and the refinement options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <em>after</em> <c>AddCharterModels()</c>: the refiner resolves
    /// <c>IModelClientFactory</c> from there rather than building its own model client.
    /// </para>
    /// <para>
    /// <see cref="RefinementOptions"/> is registered with defaults only if the host has not already
    /// supplied one, so a host projecting <c>CharterConfig</c> into it can register first and win.
    /// <see cref="RefinementContext"/> is deliberately <em>not</em> registered: it is per-repository,
    /// loaded from that repo's committed <c>.charter/</c> folder and the organisation's standards
    /// repo, and passing it per call is what keeps one repo's guardrails from leaking into another's.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterRefinement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);
        services.TryAddSingleton(new RefinementOptions());
        services.TryAddSingleton<RefinementPromptBuilder>();
        services.TryAddScoped<ISpecRefiner, SpecRefiner>();

        return services;
    }
}
