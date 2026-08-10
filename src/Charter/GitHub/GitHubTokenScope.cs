namespace Charter.GitHub;

/// <summary>
/// The permission set an installation token is minted with.
/// </summary>
/// <remarks>
/// <para>
/// A GitHub App's installation token defaults to every permission the installation was granted.
/// Section 7.4 wants the opposite: the narrowest token that does the job, so a recon run that only
/// reads cannot write, and a token that writes code cannot administer the repository. Each scope
/// below is a separate cache entry, so widening is a deliberate call rather than a side effect of
/// whichever code path asked first.
/// </para>
/// <para>
/// Nothing here asks for <c>administration</c>, <c>secrets</c>, <c>actions</c> or <c>members</c>.
/// Charter has no merge button and no reason to read a secret.
/// </para>
/// </remarks>
public sealed record GitHubTokenScope
{
    private GitHubTokenScope(string name, IReadOnlyDictionary<string, string> permissions)
    {
        Name = name;
        Permissions = permissions;
    }

    /// <summary>Reads code and metadata. What a recon run and a <c>.charter/</c> load get.</summary>
    public static GitHubTokenScope ReadOnly { get; } = new(
        "read-only",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contents"] = "read",
            ["metadata"] = "read",
            ["pull_requests"] = "read",
            ["checks"] = "read",
        });

    /// <summary>
    /// Writes a branch and opens a pull request. What the scope-config proposal and an agent session
    /// get. It cannot merge: merge authority is branch protection and CODEOWNERS (section 7.5).
    /// </summary>
    public static GitHubTokenScope Contribute { get; } = new(
        "contribute",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contents"] = "write",
            ["metadata"] = "read",
            ["pull_requests"] = "write",
            ["checks"] = "read",
        });

    /// <summary>
    /// Reads the repository's administrative settings, and nothing else. What the merge gate check
    /// of change spec 001 part A.5 needs: GitHub will not report a branch protection rule to a token
    /// without <c>administration</c>, and verifying that protection is <em>configured</em> rather
    /// than merely supported is the whole point of the onboarding step.
    /// </summary>
    /// <remarks>
    /// Read, never write. An installation that was not granted <c>administration</c> gets a token
    /// without it and the check reports "not verified", which is the correct answer — it is never
    /// reported as protected.
    /// </remarks>
    public static GitHubTokenScope Inspect { get; } = new(
        "inspect",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["administration"] = "read",
            ["metadata"] = "read",
        });

    /// <summary>
    /// Administers the repository: creating one, transferring one, applying branch protection
    /// (sections 26.9, 26.10, and part A.2's optional operations).
    /// </summary>
    /// <remarks>
    /// The only scope that can write a setting, and it is reachable only through operations that a
    /// capability gates and an admin triggers. It still cannot merge: there is no permission that
    /// would let it, and no code path that asks.
    /// </remarks>
    public static GitHubTokenScope Administer { get; } = new(
        "administer",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["administration"] = "write",
            ["metadata"] = "read",
        });

    /// <summary>Registers the repository webhook, and nothing else.</summary>
    public static GitHubTokenScope Webhooks { get; } = new(
        "webhooks",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repository_hooks"] = "write",
            ["metadata"] = "read",
        });

    /// <summary>The scopes an ordinary session or request path may use.</summary>
    /// <remarks>
    /// Neither can administer anything. The privileged scopes above are deliberately not in this
    /// list, so a test can assert the everyday paths stay narrow without asserting that the optional
    /// operations do not exist.
    /// </remarks>
    public static IReadOnlyList<GitHubTokenScope> Everyday { get; } = [ReadOnly, Contribute];

    /// <summary>A short name, used as part of the cache key and in logs.</summary>
    public string Name { get; }

    /// <summary>The <c>permissions</c> block sent with the token request.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
