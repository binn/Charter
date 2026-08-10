namespace Charter.Adapters;

/// <summary>
/// A credential that currently exists and is usable, reduced to what compatibility resolution needs.
/// </summary>
/// <param name="Kind">A <c>CredentialGrant.kind</c> value (section 20b.2), for example
/// <c>anthropic_api_key</c>.</param>
/// <param name="Endpoint">
/// The credential's base URL, where it points at a gateway or a self-hosted endpoint. Carried through
/// so the UI can say <em>which</em> credential a combination would run on.
/// </param>
public sealed record AvailableCredential(string Kind, string? Endpoint = null);

/// <summary>
/// The credentials resolution should consider — the ones section 20b.3 would actually reach for.
/// </summary>
/// <remarks>
/// Charter's <c>CredentialGrant</c> entity and the resolution chain live outside this module. Binding
/// this interface to them is the orchestrator's job; everything here needs is a list of kinds that
/// are present and not <c>exhausted</c>, <c>invalid</c>, or <c>revoked</c>.
/// </remarks>
public interface ICredentialInventory
{
    IReadOnlyList<AvailableCredential> AvailableCredentials { get; }
}

/// <summary>
/// What the repository's <c>.charter/config.yml</c> permits (section 8).
/// </summary>
/// <remarks>
/// Bound by the orchestrator to the parsed repo config. An empty allow list means "no restriction";
/// a deny list always wins over an allow list. Patterns are matched ordinally and may end in
/// <c>*</c>, so <c>anthropic/*</c> allows every Anthropic model and <c>*</c> allows everything.
/// </remarks>
public interface IRepoModelPolicy
{
    IReadOnlyList<string> AllowedAdapterIds { get; }

    IReadOnlyList<string> AllowedModels { get; }

    IReadOnlyList<string> DeniedModels { get; }
}

/// <summary>A repo policy that permits everything, and the base for narrower ones.</summary>
public sealed record RepoModelPolicy(
    IReadOnlyList<string> AllowedAdapterIds,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<string> DeniedModels) : IRepoModelPolicy
{
    public static RepoModelPolicy Unrestricted { get; } = new([], [], []);
}

/// <summary>An in-memory credential inventory, for tests and for the instance-level fallback.</summary>
public sealed record CredentialInventory(IReadOnlyList<AvailableCredential> AvailableCredentials)
    : ICredentialInventory
{
    public static CredentialInventory Empty { get; } = new([]);

    public static CredentialInventory OfKinds(params string[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return new CredentialInventory([.. kinds.Select(kind => new AvailableCredential(kind))]);
    }
}

/// <summary>A pairing the UI may offer.</summary>
public sealed record AdapterModelOption(
    string AdapterId,
    string Model,
    string Provider,
    string CredentialKind,
    string? Endpoint);

/// <summary>Why a pairing is not on offer.</summary>
public enum AdapterModelExclusionReason
{
    /// <summary>The repository does not permit this adapter.</summary>
    AdapterNotPermitted,

    /// <summary>The repository's deny list names this model.</summary>
    ModelDenied,

    /// <summary>The repository has an allow list and this model is not on it.</summary>
    ModelNotAllowed,

    /// <summary>The agent CLI cannot authenticate against this model's provider at all.</summary>
    AdapterCannotUseProvider,

    /// <summary>The adapter could use this provider, but no usable credential for it exists.</summary>
    NoCredential,
}

/// <summary>A pairing that was considered and rejected, with the reason in plain English.</summary>
public sealed record AdapterModelExclusion(
    string AdapterId,
    string Model,
    AdapterModelExclusionReason Reason,
    string Explanation);

/// <summary>The full result: what may be offered, and why everything else may not.</summary>
public sealed record AdapterCompatibility(
    IReadOnlyList<AdapterModelOption> Options,
    IReadOnlyList<AdapterModelExclusion> Exclusions)
{
    public static AdapterCompatibility Empty { get; } = new([], []);

    /// <summary>True when there is no valid combination at all — the case that needs its own UI.</summary>
    public bool IsEmpty => Options.Count == 0;

    public IEnumerable<string> AdapterIds => Options.Select(option => option.AdapterId).Distinct(StringComparer.Ordinal);

    public IReadOnlyList<string> ModelsFor(string adapterId)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        return
        [
            .. Options
                .Where(option => string.Equals(option.AdapterId, adapterId, StringComparison.Ordinal))
                .Select(option => option.Model)
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
