using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Charter.Configuration;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>Mints and caches single-repository installation tokens (section 7.4).</summary>
public interface IGitHubAppTokenProvider
{
    /// <summary>
    /// A token for exactly this repository, from cache when one is still comfortably valid.
    /// </summary>
    Task<GitHubInstallationToken> GetInstallationTokenAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A token for an installation rather than for one repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one exception to section 7.4's "always one repository" rule, and it exists for exactly one
    /// caller: creating a repository (section 26.10), which cannot name a repository that does not
    /// exist yet. It is never cached and never handed to a runner — a runner's credential comes from
    /// <see cref="GetInstallationTokenAsync"/>, which cannot widen past the repository it names.
    /// </para>
    /// <para>
    /// Every other caller wants the repository-scoped method. This one is deliberately awkward to
    /// reach and deliberately loud in the audit log.
    /// </para>
    /// </remarks>
    Task<GitHubInstallationToken> GetOrganizationTokenAsync(
        long installationId,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a cached token, so the next call mints a fresh one. Called when GitHub answers 401,
    /// which is what a revoked installation or a rotated key looks like from here.
    /// </summary>
    void Invalidate(GitHubRepository repository, GitHubTokenScope scope);
}

/// <summary>
/// The GitHub App authentication chain: PEM key to app JWT to single-repository installation token.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.4 is the whole design. The app JWT never leaves this class; what callers get is an
/// installation token naming one repository, minted with the narrowest permission set the caller
/// asked for, and cached only until shortly before it expires. No token value is ever logged — the
/// log lines here name a repository, a scope and an expiry, and nothing else.
/// </para>
/// <para>
/// Registered as a singleton so the cache is shared, with a per-key gate so twenty concurrent
/// sessions against one repository mint one token rather than twenty.
/// </para>
/// </remarks>
public sealed class GitHubAppTokenProvider : IGitHubAppTokenProvider, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> this provider resolves.</summary>
    public const string HttpClientName = "charter-github";

    private readonly ConcurrentDictionary<string, GitHubInstallationToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubConfig _config;
    private readonly GitHubOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubAppTokenProvider> _logger;

    public GitHubAppTokenProvider(
        IHttpClientFactory httpClientFactory,
        GitHubConfig config,
        GitHubOptions options,
        TimeProvider clock,
        ILogger<GitHubAppTokenProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _config = config;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How many tokens are currently cached. For tests and diagnostics; never a token.</summary>
    public int CachedTokenCount => _cache.Count;

    /// <inheritdoc />
    public async Task<GitHubInstallationToken> GetInstallationTokenAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(scope);

        var key = CacheKey(repository, scope);

        if (_cache.TryGetValue(key, out var cached)
            && cached.IsUsableAt(_clock.GetUtcNow(), _options.TokenRefreshMargin))
        {
            return cached;
        }

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Another caller may have minted one while this call waited on the gate.
            if (_cache.TryGetValue(key, out cached)
                && cached.IsUsableAt(_clock.GetUtcNow(), _options.TokenRefreshMargin))
            {
                return cached;
            }

            var minted = await MintAsync(repository, scope, cancellationToken);
            _cache[key] = minted;

            _logger.LogInformation(
                "Minted a GitHub installation token for {Repository} ({Scope}); expires {ExpiresAt:O}",
                minted.Repository,
                minted.Scope,
                minted.ExpiresAt);

            return minted;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GitHubInstallationToken> GetOrganizationTokenAsync(
        long installationId,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        ArgumentNullException.ThrowIfNull(scope);

        // Not cached, on purpose. A repository-wide token that lived in a dictionary would be the
        // easiest thing in this class to reach for by accident, and the whole design rests on that
        // being hard.
        _logger.LogInformation(
            "Minting an installation-wide GitHub token for installation {InstallationId} ({Scope}); "
            + "this is the repository-creation path and nothing else uses it",
            installationId,
            scope.Name);

        return await MintAsync(installationId, repository: null, scope, cancellationToken);
    }

    /// <inheritdoc />
    public void Invalidate(GitHubRepository repository, GitHubTokenScope scope)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(scope);

        _cache.TryRemove(CacheKey(repository, scope), out _);
    }

    /// <summary>Builds the app JWT for the current instant. Internal: it must not leave the assembly.</summary>
    internal string CreateAppJwt() => GitHubAppJwt.Create(
        _config.AppId,
        _config.PrivateKeyPem.Reveal(),
        _clock.GetUtcNow(),
        _options.AppJwtLifetime,
        _options.AppJwtBackdate);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
    }

    private static string CacheKey(GitHubRepository repository, GitHubTokenScope scope)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{repository.InstallationId}|{repository.FullName}|{scope.Name}");

    private Task<GitHubInstallationToken> MintAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken)
        => MintAsync(repository.InstallationId, repository, scope, cancellationToken);

    private async Task<GitHubInstallationToken> MintAsync(
        long installationId,
        GitHubRepository? repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var path = new Uri(
            _options.ApiBaseUrl,
            string.Create(
                CultureInfo.InvariantCulture,
                $"app/installations/{installationId}/access_tokens"));

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion);

        // Section 7.4: one repository, and only the permissions this unit of work needs. GitHub
        // takes the bare name here, not owner/name. The one caller that passes no repository is
        // repository creation, which has no name to scope to yet.
        request.Content = repository is null
            ? JsonContent.Create(new { permissions = scope.Permissions })
            : JsonContent.Create(new
            {
                repositories = new[] { repository.Name },
                permissions = scope.Permissions,
            });

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Deliberately not reading the body: this exchange's request content is a permission
            // grant and its failure bodies echo it.
            throw GitHubApiException.ForResponse(response, request);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = ParseOrThrow(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || tokenElement.GetString() is not { Length: > 0 } token)
        {
            throw new GitHubApiException("GitHub's installation token response carried no token.");
        }

        // A token GitHub says covers every repository is not the token section 7.4 describes, and
        // handing it to a runner would silently widen the blast radius of a compromised session.
        // Checked only for the repository-scoped path: the creation path asked for exactly this and
        // its token never leaves the control plane.
        if (repository is not null
            && root.TryGetProperty("repository_selection", out var selection)
            && selection.ValueKind == JsonValueKind.String
            && string.Equals(selection.GetString(), "all", StringComparison.Ordinal))
        {
            throw new GitHubApiException(
                $"GitHub minted a token covering every repository in the installation rather than "
                + $"only {repository.FullName}. Charter refuses to use it (section 7.4).");
        }

        var expiresAt = root.TryGetProperty("expires_at", out var expiry)
                        && expiry.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(
                            expiry.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var parsed)
            ? parsed
            : _clock.GetUtcNow().AddHours(1);

        return new GitHubInstallationToken
        {
            Token = new Secret(token),
            ExpiresAt = expiresAt,
            Repository = repository?.FullName
                         ?? string.Create(CultureInfo.InvariantCulture, $"installation/{installationId}"),
            Scope = scope.Name,
            Permissions = ReadPermissions(root),
        };
    }

    private static JsonDocument ParseOrThrow(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException("GitHub's installation token response was not JSON.", ex);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadPermissions(JsonElement root)
    {
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root.TryGetProperty("permissions", out var block) && block.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in block.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    permissions[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return permissions;
    }
}

/// <summary>Mints the credential a runner is handed (section 7.4).</summary>
/// <remarks>
/// A separate seam from <see cref="IGitHubAppTokenProvider"/> on purpose: the execution plane is
/// given <em>this</em>, which returns a credential with no renewal path, rather than the provider,
/// which can mint indefinitely.
/// </remarks>
public interface IGitHubRunnerCredentialFactory
{
    /// <summary>A use-once, single-repository, non-renewable credential.</summary>
    Task<GitHubRunnerCredential> IssueAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class GitHubRunnerCredentialFactory : IGitHubRunnerCredentialFactory
{
    private readonly IGitHubAppTokenProvider _tokens;

    public GitHubRunnerCredentialFactory(IGitHubAppTokenProvider tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    /// <inheritdoc />
    public async Task<GitHubRunnerCredential> IssueAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        CancellationToken cancellationToken = default)
        => GitHubRunnerCredential.From(
            await _tokens.GetInstallationTokenAsync(repository, scope, cancellationToken));
}

/// <summary>Status codes this integration reacts to by name rather than by number.</summary>
internal static class GitHubStatus
{
    internal static bool IsAuthFailure(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
