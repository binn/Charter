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

    /// <summary>A short name, used as part of the cache key and in logs.</summary>
    public string Name { get; }

    /// <summary>The <c>permissions</c> block sent with the token request.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
