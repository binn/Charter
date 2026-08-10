namespace Charter.Configuration;

/// <summary>
/// One OAuth provider, enabled only when both halves of its credential pair are present.
/// </summary>
/// <param name="Name">Provider key: <c>github</c>, <c>google</c>, <c>discord</c>, <c>slack</c>.</param>
/// <param name="ClientId">The provider's client / application ID.</param>
/// <param name="ClientSecret">The provider's client secret.</param>
public sealed record OAuthProviderConfig(string Name, string ClientId, Secret ClientSecret);

/// <summary>
/// Identity providers behind the single <c>IIdentityProvider</c> seam of section 21.
/// </summary>
public sealed record AuthConfig
{
    /// <summary>Every provider with both an ID and a secret configured.</summary>
    public required IReadOnlyList<OAuthProviderConfig> OAuthProviders { get; init; }

    /// <summary><c>CHARTER_SAML_METADATA_URL</c>. Organization mode only.</summary>
    public Uri? SamlMetadataUrl { get; init; }

    /// <summary>True when any sign-in method beyond the first-run admin account is configured.</summary>
    public bool HasAnyProvider => OAuthProviders.Count > 0 || SamlMetadataUrl is not null;

    /// <summary>Looks a provider up by name, case-insensitively.</summary>
    public OAuthProviderConfig? Provider(string name)
        => OAuthProviders.FirstOrDefault(
            provider => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Provider keys, in the order section 4.2 lists them.</summary>
    internal static IReadOnlyList<(string Name, string Prefix)> Providers { get; } =
    [
        ("github", "CHARTER_OAUTH_GITHUB"),
        ("google", "CHARTER_OAUTH_GOOGLE"),
        ("discord", "CHARTER_OAUTH_DISCORD"),
        ("slack", "CHARTER_OAUTH_SLACK"),
    ];

    internal static AuthConfig Parse(EnvReader reader, CharterMode mode)
    {
        var configured = new List<OAuthProviderConfig>();

        foreach (var (name, prefix) in Providers)
        {
            var idVariable = $"{prefix}_ID";
            var secretVariable = $"{prefix}_SECRET";
            var id = reader.Optional(idVariable);
            var secret = reader.OptionalSecret(secretVariable);

            if (id is not null && secret is not null)
            {
                configured.Add(new OAuthProviderConfig(name, id, secret));
            }
            else if (id is not null)
            {
                // Half a pair is a typo or a half-finished rollout, never an intent to disable the
                // provider. Ignoring it silently means the operator finds out at the sign-in screen.
                reader.Error(
                    secretVariable,
                    $"{idVariable} is set but {secretVariable} is not. Set both to enable the " +
                    $"{name} provider, or neither to leave it disabled.");
            }
            else if (secret is not null)
            {
                reader.Error(
                    idVariable,
                    $"{secretVariable} is set but {idVariable} is not. Set both to enable the " +
                    $"{name} provider, or neither to leave it disabled.");
            }
        }

        var saml = reader.Url(
            "CHARTER_SAML_METADATA_URL",
            "an absolute http(s) URL to the identity provider's SAML metadata document",
            required: false);

        if (saml is not null && mode == CharterMode.Personal)
        {
            reader.Warn(
                "CHARTER_SAML_METADATA_URL",
                "CHARTER_SAML_METADATA_URL is set but CHARTER_MODE is personal; SAML applies to " +
                "organization mode only (section 4.2). Set CHARTER_MODE=organization to use it.");
        }

        return new AuthConfig { OAuthProviders = configured, SamlMetadataUrl = saml };
    }
}
