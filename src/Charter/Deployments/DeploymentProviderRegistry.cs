namespace Charter.Deployments;

/// <summary>
/// The deployment providers this instance has, which in Phase 1 is Railway or nothing.
/// </summary>
/// <remarks>
/// <para>
/// A registry rather than a single injected provider for one reason: <em>nothing</em> is a valid
/// configuration. A self-hoster on Render, Fly or Coolify runs with no provider at all and binds
/// previews through <c>POST /api/deployments/{prSha}</c>; every consumer therefore has to handle
/// <see cref="Configured"/> being null, and a nullable dependency in a constructor is a much easier
/// thing to forget than a property that is obviously nullable at the call site.
/// </para>
/// <para>
/// <see cref="Configured"/> is instance-wide. Per-repository selection belongs with change spec 001
/// Part A, where a repository gains a version control provider and a project type; adding it now
/// would be a settings surface for a choice nobody can yet make, since Phase 1 ships one
/// implementation.
/// </para>
/// </remarks>
public sealed class DeploymentProviderRegistry
{
    private readonly IReadOnlyList<IDeploymentProvider> _providers;

    public DeploymentProviderRegistry(IEnumerable<IDeploymentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];
    }

    /// <summary>Every registered provider, in registration order.</summary>
    public IReadOnlyList<IDeploymentProvider> All => _providers;

    /// <summary>The provider this instance polls and tears down with, or null when it has none.</summary>
    public IDeploymentProvider? Configured => _providers.Count > 0 ? _providers[0] : null;

    /// <summary>The provider with this id, or null. Ids are compared case-insensitively.</summary>
    public IDeploymentProvider? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
