using Charter.Auth.Providers;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the control plane's half of the Charter Agent (section 33).
/// </summary>
/// <remarks>
/// Called from <c>AddCharterRunners</c> when <c>CHARTER_RUNNER</c> includes <c>agent</c>, and
/// separately callable so a test can stand up the agent plane without the rest of the execution
/// plane. Placed in <c>Microsoft.Extensions.DependencyInjection</c> to match every other
/// <c>AddCharter*</c>.
/// </remarks>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Adds pairing, the connection registry, and the <see cref="AgentRunner"/> backend.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">Overrides for the timing contract handed to every agent.</param>
    /// <remarks>
    /// <para>
    /// Requires <c>AddCharterData</c> for the DbContext and the job queue. The password hasher is
    /// registered defensively rather than assumed: agent credentials are hashed with the same PBKDF2
    /// construction as passwords, and <c>AddCharterAuth</c> registering it first is the normal case
    /// rather than a guarantee.
    /// </para>
    /// <para>
    /// <see cref="AgentRunner"/> is registered as an <c>IAgentRunner</c>, which is what puts it in the
    /// registry and makes the dispatcher route to it by capability match (section 27.3).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterAgentPlane(
        this IServiceCollection services,
        Action<AgentPlaneOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICharterPasswordHasher>(_ => new CharterPasswordHasher());
        services.TryAddSingleton<AgentCredentialMint>();
        services.TryAddSingleton<AgentConnectionRegistry>();

        services.TryAddSingleton(_ =>
        {
            var options = new AgentPlaneOptions();
            configure?.Invoke(options);
            return options;
        });

        services.AddScoped<AgentPlaneService>();

        services.AddSingleton<IAgentRunner>(provider => new AgentRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<AgentConnectionRegistry>(),
            provider.GetRequiredService<AgentPlaneOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AgentRunner>>()));

        return services;
    }
}
