using Charter.Domain;

namespace Charter.Auth.Providers;

/// <summary>
/// Everything that differs between one OAuth provider and the next, as data.
/// </summary>
/// <remarks>
/// Section 21 lists four OAuth providers and Charter will be asked for more. They differ in three
/// endpoints, a scope list and where the subject sits in the user document — none of which is
/// behaviour. Keeping them as descriptors means adding a provider is a row in
/// <see cref="OAuthProviderDescriptors"/>, not a class, and means the seam does not grow a shape per
/// vendor.
/// </remarks>
/// <param name="Name">The key used in configuration and URLs.</param>
/// <param name="Kind">The <see cref="IdentityProviderKind"/> the linked identity is stored under.</param>
/// <param name="AuthorizationEndpoint">Where the person is sent.</param>
/// <param name="TokenEndpoint">Where the authorisation code is exchanged.</param>
/// <param name="UserEndpoint">Where the subject is read from.</param>
/// <param name="Scopes">The minimum that yields a stable subject and an email.</param>
/// <param name="SubjectPath">Dotted path to the subject id in the user document.</param>
/// <param name="EmailPath">Dotted path to the email, when the provider returns one.</param>
/// <param name="DisplayNamePath">Dotted path to a human name.</param>
public sealed record OAuthProviderDescriptor(
    string Name,
    IdentityProviderKind Kind,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri UserEndpoint,
    IReadOnlyList<string> Scopes,
    string SubjectPath,
    string EmailPath,
    string DisplayNamePath);

/// <summary>The four providers section 21 names.</summary>
/// <remarks>
/// Slack and Discord pull double duty: the identity link maps a Slack or Discord user to a Charter
/// requester, which is what makes inbound requests from those platforms resolve to a person.
/// </remarks>
public static class OAuthProviderDescriptors
{
    public static OAuthProviderDescriptor GitHub { get; } = new(
        "github",
        IdentityProviderKind.GitHub,
        new Uri("https://github.com/login/oauth/authorize"),
        new Uri("https://github.com/login/oauth/access_token"),
        new Uri("https://api.github.com/user"),
        ["read:user", "user:email"],
        SubjectPath: "id",
        // GitHub returns null here for anyone with a private email; the linker then has no email to
        // match on and falls back to an already-linked identity, which is the correct refusal.
        EmailPath: "email",
        DisplayNamePath: "name");

    public static OAuthProviderDescriptor Google { get; } = new(
        "google",
        IdentityProviderKind.Google,
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        new Uri("https://openidconnect.googleapis.com/v1/userinfo"),
        ["openid", "email", "profile"],
        SubjectPath: "sub",
        EmailPath: "email",
        DisplayNamePath: "name");

    public static OAuthProviderDescriptor Discord { get; } = new(
        "discord",
        IdentityProviderKind.Discord,
        new Uri("https://discord.com/oauth2/authorize"),
        new Uri("https://discord.com/api/oauth2/token"),
        new Uri("https://discord.com/api/users/@me"),
        ["identify", "email"],
        SubjectPath: "id",
        EmailPath: "email",
        DisplayNamePath: "global_name");

    public static OAuthProviderDescriptor Slack { get; } = new(
        "slack",
        IdentityProviderKind.Slack,
        new Uri("https://slack.com/openid/connect/authorize"),
        new Uri("https://slack.com/api/openid.connect.token"),
        new Uri("https://slack.com/api/openid.connect.userInfo"),
        ["openid", "email", "profile"],
        SubjectPath: "sub",
        EmailPath: "email",
        DisplayNamePath: "name");

    /// <summary>All four, in the order section 4.2 lists them.</summary>
    public static IReadOnlyList<OAuthProviderDescriptor> All { get; } = [GitHub, Google, Discord, Slack];

    /// <summary>Looks a descriptor up by its configuration key, case-insensitively.</summary>
    public static OAuthProviderDescriptor? Find(string name)
        => All.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase));
}
