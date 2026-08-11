using Charter.Configuration;
using Charter.Data.Credentials;
using Charter.Models;
using Charter.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// Namespace deliberately Charter.Data rather than Charter.Data.Credentials: the host already has a
// `using Charter.Data;` for AddCharterData, so wiring credentials up costs one line rather than two.
namespace Charter.Data;

/// <summary>
/// Registers credential encryption at rest and the EF-backed credential store (section 20b.2).
/// </summary>
public static class CredentialServiceCollectionExtensions
{
    /// <summary>
    /// Closes the seam <c>AddCharterModels</c> leaves open: binds <see cref="IModelCredentialStore"/>
    /// to <see cref="CredentialGrant"/> and registers the AES-256-GCM protector that decrypts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <c>AddCharterConfig</c> (for <see cref="KeyConfig"/>) and <c>AddCharterData</c> (for
    /// <see cref="CharterDbContext"/>) to have been called on the same collection. Without this
    /// method the container cannot construct <c>CredentialResolver</c> at all, which is a startup
    /// failure rather than a runtime one - the model layer's registrations name
    /// <see cref="IModelCredentialStore"/> as the host's to provide.
    /// </para>
    /// <para>
    /// The protector is a singleton: deriving the key is startup work, and the type holds no
    /// per-request state. The store is scoped, because <see cref="CharterDbContext"/> is.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterCredentials(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ICredentialProtector>(provider =>
            new AesGcmCredentialProtector(provider.GetRequiredService<KeyConfig>()));

        // Section 20b.3 tiers 4 and 5, read from the environment rather than from a row. Registered
        // as a singleton because the exhaustion and invalidity a 429 or a 401 records against a
        // variable have to outlive the request that discovered them.
        services.TryAddSingleton(provider =>
        {
            var models = provider.GetRequiredService<CharterConfig>().Models;

            return InstanceModelCredentials.From(
                models.AnthropicApiKey?.Reveal(),
                models.OpenRouterApiKey?.Reveal());
        });

        services.TryAddScoped<EfModelCredentialStore>();

        // The EF store is wrapped rather than replaced. ANTHROPIC_API_KEY and OPENROUTER_API_KEY
        // satisfied startup validation and the section 30.1 preflight check and were then never
        // consulted again, because resolution read credential_grants and nothing else - so the
        // documented default install could not make a single model call. This is the line that puts
        // them in the chain.
        services.TryAddScoped<IModelCredentialStore>(provider => new InstanceKeyModelCredentialStore(
            provider.GetRequiredService<EfModelCredentialStore>(),
            provider.GetRequiredService<InstanceModelCredentials>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<InstanceKeyModelCredentialStore>>()));

        return services;
    }
}
