using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Charter.Configuration;

namespace Charter.Auth.Providers;

/// <summary>The two network calls an OAuth callback needs, behind a seam so the flow is testable.</summary>
public interface IOAuthExchange
{
    /// <summary>Trades an authorisation code for an access token.</summary>
    Task<string?> ExchangeCodeAsync(
        OAuthProviderDescriptor descriptor,
        OAuthProviderConfig config,
        string code,
        Uri redirectUri,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the provider's user document.</summary>
    Task<JsonElement?> FetchUserAsync(
        OAuthProviderDescriptor descriptor,
        string accessToken,
        CancellationToken cancellationToken = default);
}

/// <summary>The HTTP implementation of <see cref="IOAuthExchange"/>.</summary>
/// <remarks>
/// Every provider here speaks the same two requests: a form post to the token endpoint asking for
/// JSON, and a bearer-authenticated GET of the user endpoint. Anything a specific provider needs
/// beyond that belongs in its <see cref="OAuthProviderDescriptor"/>, not in a subclass.
/// </remarks>
public sealed class HttpOAuthExchange : IOAuthExchange
{
    /// <summary>The named <see cref="HttpClient"/> this uses.</summary>
    public const string HttpClientName = "charter-oauth";

    private readonly HttpClient client;

    public HttpOAuthExchange(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <inheritdoc />
    public async Task<string?> ExchangeCodeAsync(
        OAuthProviderDescriptor descriptor,
        OAuthProviderConfig config,
        string code,
        Uri redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(redirectUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, descriptor.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret.Reveal(),
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
            }),
        };

        // GitHub answers form-encoded unless asked otherwise; everyone else answers JSON regardless.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return payload.ValueKind == JsonValueKind.Object
               && payload.TryGetProperty("access_token", out var token)
               && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }

    /// <inheritdoc />
    public async Task<JsonElement?> FetchUserAsync(
        OAuthProviderDescriptor descriptor,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.UserEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Charter");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return payload.ValueKind == JsonValueKind.Object ? payload.Clone() : null;
    }
}

/// <summary>
/// The short-lived <c>state</c> values that tie an authorisation redirect to the callback that
/// answers it. Without this, the callback accepts anyone's code — the classic OAuth CSRF.
/// </summary>
public interface IOAuthStateStore
{
    /// <summary>Mints a state value for a provider and remembers where to return afterwards.</summary>
    string Issue(string providerName, Uri? returnUrl);

    /// <summary>Consumes a state value exactly once; false when it is unknown, stale or already used.</summary>
    bool TryConsume(string providerName, string state, out Uri? returnUrl);
}

/// <summary>In-process, time-bounded, single-use state storage.</summary>
/// <remarks>
/// In memory because it is worthless after five minutes and Charter adds no Redis (spec §2.3). A
/// restart mid-redirect invalidates the state, which the sign-in page reports as "try again" — the
/// right trade for not adding a service or a table to hold thirty seconds of data.
/// </remarks>
public sealed class OAuthStateStore : IOAuthStateStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    public OAuthStateStore(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <inheritdoc />
    public string Issue(string providerName, Uri? returnUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        Prune();

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        pending[Key(providerName, state)] = new Pending(clock.GetUtcNow(), returnUrl);

        return state;
    }

    /// <inheritdoc />
    public bool TryConsume(string providerName, string state, out Uri? returnUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        returnUrl = null;

        if (string.IsNullOrWhiteSpace(state) || !pending.TryRemove(Key(providerName, state), out var entry))
        {
            return false;
        }

        if (clock.GetUtcNow() - entry.IssuedAt > Lifetime)
        {
            return false;
        }

        returnUrl = entry.ReturnUrl;
        return true;
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Lifetime;

        foreach (var (key, entry) in pending)
        {
            if (entry.IssuedAt < cutoff)
            {
                pending.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string providerName, string state) => $"{providerName}:{state}";

    private sealed record Pending(DateTimeOffset IssuedAt, Uri? ReturnUrl);
}
