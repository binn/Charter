using Charter.Configuration;
using Charter.Data.Credentials;
using Charter.Models;
using Charter.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.TryAddScoped<IModelCredentialStore, EfModelCredentialStore>();

        return services;
    }
}
