using System.Globalization;
using System.Text.Json;
using Charter.Configuration;
using Charter.Domain;

namespace Charter.Auth.Providers;

/// <summary>
/// One OAuth provider, driven entirely by its <see cref="OAuthProviderDescriptor"/>.
/// </summary>
/// <remarks>
/// There is one of these per configured provider rather than one class per vendor. GitHub, Google,
/// Discord and Slack differ only in the descriptor, and a fifth would too — which is the point of
/// section 21's single seam.
/// </remarks>
public sealed class OAuthIdentityProvider : IIdentityProvider
{
    private readonly OAuthProviderDescriptor descriptor;
    private readonly OAuthProviderConfig config;
    private readonly IOAuthExchange exchange;
    private readonly IOAuthStateStore states;
    private readonly Uri baseUrl;

    public OAuthIdentityProvider(
        OAuthProviderDescriptor descriptor,
        OAuthProviderConfig config,
        IOAuthExchange exchange,
        IOAuthStateStore states,
        Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(baseUrl);

        this.descriptor = descriptor;
        this.config = config;
        this.exchange = exchange;
        this.states = states;
        this.baseUrl = baseUrl;
    }

    /// <inheritdoc />
    public IdentityProviderKind Kind => descriptor.Kind;

    /// <inheritdoc />
    public string Name => descriptor.Name;

    /// <inheritdoc />
    public IdentityProviderStyle Style => IdentityProviderStyle.Redirect;

    /// <inheritdoc />
    public bool RequiresOrganizationMode => false;

    /// <summary>The callback URL this provider must be configured with.</summary>
    public Uri CallbackUri => new(baseUrl, $"/api/auth/{descriptor.Name}/callback");

    /// <inheritdoc />
    public Task<IdentityAuthenticationResult> BeginAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var state = states.Issue(descriptor.Name, attempt.ReturnUrl);
        var redirectUri = attempt.RedirectUri ?? CallbackUri;

        var query = string.Join('&',
            $"response_type=code",
            $"client_id={Uri.EscapeDataString(config.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}",
            $"scope={Uri.EscapeDataString(string.Join(' ', descriptor.Scopes))}",
            $"state={Uri.EscapeDataString(state)}");

        var separator = string.IsNullOrEmpty(descriptor.AuthorizationEndpoint.Query) ? '?' : '&';
        var location = new Uri($"{descriptor.AuthorizationEndpoint}{separator}{query}");

        return Task.FromResult<IdentityAuthenticationResult>(
            new IdentityAuthenticationResult.RedirectRequired(location, state));
    }

    /// <inheritdoc />
    public async Task<IdentityAuthenticationResult> CompleteAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (!attempt.Parameters.TryGetValue("state", out var state)
            || !states.TryConsume(descriptor.Name, state ?? string.Empty, out _))
        {
            // Single-use, so a replayed callback fails here as well as a forged one.
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.MalformedAttempt,
                "That sign-in link has expired. Start again.");
        }

        if (!attempt.Parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.ProviderRejected,
                $"The {descriptor.Name} sign-in was cancelled or refused.");
        }

        var token = await exchange.ExchangeCodeAsync(
            descriptor,
            config,
            code,
            attempt.RedirectUri ?? CallbackUri,
            cancellationToken);

        if (token is null)
        {
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.ProviderRejected,
                $"{descriptor.Name} would not issue an access token.");
        }

        var document = await exchange.FetchUserAsync(descriptor, token, cancellationToken);
        if (document is not { } user)
        {
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.ProviderRejected,
                $"{descriptor.Name} would not return the account details.");
        }

        var subject = ReadPath(user, descriptor.SubjectPath);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.ProviderRejected,
                $"{descriptor.Name} did not return a stable account identifier.");
        }

        return new IdentityAuthenticationResult.Authenticated(new ExternalIdentity(
            descriptor.Kind,
            subject,
            ReadPath(user, descriptor.EmailPath),
            ReadPath(user, descriptor.DisplayNamePath)));
    }

    /// <summary>Reads a dotted path out of a provider's user document.</summary>
    /// <remarks>
    /// Dotted rather than flat because several providers nest the interesting values, and numbers are
    /// rendered invariantly because GitHub's subject is an integer and Google's is a string — the
    /// stored <c>provider_user_id</c> must not depend on which.
    /// </remarks>
    internal static string? ReadPath(JsonElement root, string path)
    {
        var current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => current.GetBoolean()
                .ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
