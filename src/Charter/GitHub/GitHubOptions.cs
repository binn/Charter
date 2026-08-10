namespace Charter.GitHub;

/// <summary>
/// Knobs for the GitHub App integration. Everything here has a working default; only a GitHub
/// Enterprise Server installation needs to change <see cref="ApiBaseUrl"/>.
/// </summary>
/// <remarks>
/// Section 4.1 keeps configuration in environment variables, and the three that matter
/// (<c>GITHUB_APP_ID</c>, <c>GITHUB_APP_PRIVATE_KEY</c>, <c>GITHUB_WEBHOOK_SECRET</c>) arrive through
/// <see cref="Charter.Configuration.GitHubConfig"/>. This record carries the values that are protocol
/// facts rather than deployment facts, so they are constants a test can override rather than yet more
/// environment surface.
/// </remarks>
public sealed record GitHubOptions
{
    /// <summary>The REST API root. <c>https://api.github.com/</c> unless this is Enterprise Server.</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://api.github.com/");

    /// <summary>GitHub rejects a request with no <c>User-Agent</c>.</summary>
    public string UserAgent { get; init; } = "Charter";

    /// <summary>Pinned so a future default on GitHub's side cannot change a response shape under us.</summary>
    public string ApiVersion { get; init; } = "2022-11-28";

    /// <summary>
    /// How long the app JWT claims to live. GitHub refuses anything over ten minutes, so this stays
    /// comfortably under it.
    /// </summary>
    public TimeSpan AppJwtLifetime { get; init; } = TimeSpan.FromMinutes(9);

    /// <summary>
    /// How far <c>iat</c> is backdated. GitHub's own guidance, and it absorbs the clock skew between
    /// a PaaS container and GitHub without which every JWT is intermittently "issued in the future".
    /// </summary>
    public TimeSpan AppJwtBackdate { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long before expiry a cached installation token stops being handed out (section 7.4).
    /// </summary>
    /// <remarks>
    /// GitHub fixes the installation token lifetime at one hour and offers no way to shorten it, so
    /// "short TTL" is achieved by the two things Charter <em>can</em> control: the token is scoped to
    /// a single repository, and it is minted per unit of work rather than held. A runner that gets a
    /// token near the end of its hour would fail halfway through a session, so the cache abandons a
    /// token well before GitHub would.
    /// </remarks>
    public TimeSpan TokenRefreshMargin { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The most bytes a webhook delivery may carry before it is refused unread.</summary>
    public int MaxWebhookBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The most entries a single <c>.charter/</c> load will fetch.</summary>
    public int MaxCharterFolderFiles { get; init; } = 200;
}
