using Charter.Domain;

namespace Charter.VersionControl;

/// <inheritdoc />
public sealed class VersionControlProviderRegistry : IVersionControlProviderRegistry
{
    /// <summary>The provider a repository connected before repositories named one belongs to.</summary>
    /// <remarks>
    /// Phase 1 is GitHub only, and <see cref="Repo"/> carries a GitHub installation id rather than a
    /// provider column. When part A adds the second provider it adds that column too, and this
    /// constant becomes the migration default rather than the answer.
    /// </remarks>
    public const string DefaultProviderId = "github";

    private readonly IReadOnlyList<IVersionControlProvider> _providers;

    public VersionControlProviderRegistry(IEnumerable<IVersionControlProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];
    }

    /// <inheritdoc />
    public IReadOnlyList<IVersionControlProvider> Providers => _providers;

    /// <inheritdoc />
    public IVersionControlProvider? Find(string providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? null
            : _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, providerId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IVersionControlProvider For(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        return Find(DefaultProviderId)
               ?? _providers.FirstOrDefault()
               ?? throw new VersionControlCapabilityException(
                   $"No version control provider is registered, so {repo.FullName} cannot be reached.");
    }

    /// <inheritdoc />
    public VersionControlTerms TermsFor(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        return (Find(DefaultProviderId) ?? _providers.FirstOrDefault())?.Terms
               ?? VersionControlTerms.ChangeRequestDefault;
    }

    /// <inheritdoc />
    public RepoRef ReferenceFor(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        return RepoRef.For(repo, For(repo).Id);
    }
}
