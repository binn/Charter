using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Recaps;

/// <summary>Registers the engineer recap (sections 14, 7.5).</summary>
public static class RecapServiceCollectionExtensions
{
    /// <summary>
    /// Registers the recap generator, the prompt builder, the publisher and the recap options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <em>after</em> <c>AddCharterModels()</c> and <c>AddCharterGitHub()</c>: the
    /// generator resolves <c>IModelClientFactory</c> and the publisher resolves
    /// <c>IVersionControlProviderRegistry</c> rather than constructing either.
    /// </para>
    /// <para>
    /// <see cref="RecapOptions"/> is registered with defaults only if the host has not already
    /// supplied one, so a host projecting <c>CharterConfig</c> into it can register first and win.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterRecap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);
        services.TryAddSingleton(new RecapOptions());
        services.TryAddSingleton<RecapPromptBuilder>();
        services.TryAddScoped<IRecapGenerator, RecapGenerator>();
        services.TryAddScoped<IRecapPublisher, RecapPublisher>();

        return services;
    }
}
