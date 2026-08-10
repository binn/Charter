using Charter.Configuration;
using Charter.Domain;

namespace Charter.Auth.Providers;

/// <summary>
/// Every sign-in method this instance actually offers (section 21).
/// </summary>
/// <remarks>
/// <para>
/// Email and password is always present. Each OAuth provider appears only when both halves of its
/// credential pair are configured, which <see cref="AuthConfig"/> has already established — half a
/// pair is a startup error, not a silently disabled provider.
/// </para>
/// <para>
/// SAML is <b>not</b> registered in this build. The seam is shaped for it —
/// <see cref="IdentityProviderStyle.Redirect"/>, the redirect out, and the assertion arriving through
/// <see cref="IdentityAuthenticationAttempt.Parameters"/> — but shipping a provider that advertises
/// itself on the sign-in page and then fails would be worse than not offering it. See
/// <see cref="SamlConfiguredButUnavailable"/>, which the host logs at startup.
/// </para>
/// </remarks>
public sealed class IdentityProviderRegistry
{
    private readonly Dictionary<string, IIdentityProvider> byName;

    public IdentityProviderRegistry(
        PasswordIdentityProvider password,
        IEnumerable<IIdentityProvider> federated,
        bool samlConfigured = false)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(federated);

        Password = password;
        SamlConfiguredButUnavailable = samlConfigured;

        byName = new Dictionary<string, IIdentityProvider>(StringComparer.OrdinalIgnoreCase)
        {
            [password.Name] = password,
        };

        foreach (var provider in federated)
        {
            byName[provider.Name] = provider;
        }

        All = [.. byName.Values];
    }

    /// <summary>Always available (section 21).</summary>
    public PasswordIdentityProvider Password { get; }

    /// <summary>Every provider this instance offers, password first.</summary>
    public IReadOnlyList<IIdentityProvider> All { get; }

    /// <summary>
    /// True when <c>CHARTER_SAML_METADATA_URL</c> is set but this build cannot honour it, so the
    /// operator is told at startup rather than by a user hitting a dead button.
    /// </summary>
    public bool SamlConfiguredButUnavailable { get; }

    /// <summary>Looks a provider up by its URL key, or null when this instance does not offer it.</summary>
    public IIdentityProvider? Find(string name)
        => string.IsNullOrWhiteSpace(name) ? null : byName.GetValueOrDefault(name);

    /// <summary>Looks a provider up by the kind its identities are stored under.</summary>
    public IIdentityProvider? Find(IdentityProviderKind kind)
        => All.FirstOrDefault(provider => provider.Kind == kind);

    /// <summary>Builds the registry from validated configuration.</summary>
    public static IdentityProviderRegistry Build(
        AuthConfig auth,
        Uri baseUrl,
        PasswordIdentityProvider password,
        IOAuthExchange exchange,
        IOAuthStateStore states)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(baseUrl);

        var federated = new List<IIdentityProvider>();

        foreach (var configured in auth.OAuthProviders)
        {
            if (OAuthProviderDescriptors.Find(configured.Name) is { } descriptor)
            {
                federated.Add(new OAuthIdentityProvider(descriptor, configured, exchange, states, baseUrl));
            }
        }

        return new IdentityProviderRegistry(password, federated, auth.SamlMetadataUrl is not null);
    }
}
