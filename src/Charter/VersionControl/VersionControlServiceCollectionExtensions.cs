using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.VersionControl;

/// <summary>
/// Registers the version control seam (change spec 001 part A) and the services built on it.
/// </summary>
/// <remarks>
/// <para>
/// The <em>implementations</em> are registered by the provider's own extension —
/// <c>AddCharterGitHub()</c> adds <c>GitHubVersionControlProvider</c> — so this method knows nothing
/// about GitHub and a second provider is one more registration rather than an edit here.
/// </para>
/// <para>Host wiring (see <c>Program.cs</c>):</para>
/// <code>
/// builder.Services.AddCharterVersionControl();   // after AddCharterData(); AddCharterGitHub() also calls it
/// </code>
/// </remarks>
public static class VersionControlServiceCollectionExtensions
{
    /// <summary>Adds the provider registry and the change request services.</summary>
    public static IServiceCollection AddCharterVersionControl(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);

        // Singleton: providers are stateless wrappers over singleton clients, and the registry is a
        // lookup over them.
        services.TryAddSingleton<IVersionControlProviderRegistry, VersionControlProviderRegistry>();
        services.TryAddSingleton<MergeGateInspector>();

        // Scoped: both read and write rows through the request's CharterDbContext.
        services.TryAddScoped<ChangeRequestPublisher>();
        services.TryAddScoped<ChangeRequestStateTracker>();

        return services;
    }
}
