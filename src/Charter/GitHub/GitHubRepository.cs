using Charter.Domain;

namespace Charter.GitHub;

/// <summary>
/// One repository, and the installation Charter authenticates through to reach it.
/// </summary>
/// <remarks>
/// The installation id travels with the repository rather than being looked up per call, because
/// section 7.4 scopes every token to exactly one repository: there is no such thing in Charter as a
/// token for "the installation", only a token for this repository through that installation.
/// </remarks>
public sealed record GitHubRepository
{
    /// <summary>The owning user or organisation.</summary>
    public required string Owner { get; init; }

    /// <summary>The repository name on its own, which is what the token request takes.</summary>
    public required string Name { get; init; }

    /// <summary>The GitHub App installation id.</summary>
    public required long InstallationId { get; init; }

    /// <summary><c>owner/name</c>, as GitHub spells it.</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary>Parses <c>owner/name</c>.</summary>
    public static GitHubRepository Parse(string fullName, long installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);

        var trimmed = fullName.Trim().Trim('/');
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0 || slash == trimmed.Length - 1 || trimmed.IndexOf('/', slash + 1) >= 0)
        {
            throw new ArgumentException(
                $"'{fullName}' is not a GitHub repository full name; expected owner/name.",
                nameof(fullName));
        }

        return new GitHubRepository
        {
            Owner = trimmed[..slash],
            Name = trimmed[(slash + 1)..],
            InstallationId = installationId,
        };
    }

    /// <summary>The repository a connected <see cref="Repo"/> row points at.</summary>
    public static GitHubRepository For(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return Parse(repo.FullName, repo.GithubInstallationId);
    }

    /// <inheritdoc />
    public override string ToString() => FullName;
}
