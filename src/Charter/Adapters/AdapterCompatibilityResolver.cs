namespace Charter.Adapters;

/// <summary>
/// Resolves <em>(available credentials) x (adapter's supported providers) x (repo policy)</em> so the
/// UI can offer only combinations that exist (section 12b).
/// </summary>
/// <remarks>
/// <para>
/// Section 12b is explicit that silently accepting an impossible pairing and failing at dispatch is
/// the worst outcome, so this returns rejections alongside the offers. A requester told
/// <em>"your organisation has no Google credential"</em> can act on it; a session that dies four
/// minutes in with an authentication error teaches nobody anything.
/// </para>
/// <para>
/// An adapter's supported providers are derived from its <c>auth</c> block rather than declared
/// separately: the credential kinds a CLI can read <em>are</em> the providers it can build with, and a
/// second list would only ever drift out of step with the first.
/// </para>
/// </remarks>
public sealed class AdapterCompatibilityResolver
{
    /// <summary>
    /// Intersects adapters, candidate models, credentials, and repo policy.
    /// </summary>
    /// <param name="adapters">Adapters the instance has loaded.</param>
    /// <param name="candidateModels">
    /// Provider-qualified model identifiers to consider, typically the provider catalogues Charter
    /// knows about. Unqualified identifiers are Anthropic's (section 20b.1).
    /// </param>
    /// <param name="credentials">Credentials that exist and are usable right now.</param>
    /// <param name="policy">The repository's constraints, or <see cref="RepoModelPolicy.Unrestricted"/>.</param>
    public AdapterCompatibility Resolve(
        IReadOnlyList<AdapterDocument> adapters,
        IReadOnlyList<string> candidateModels,
        ICredentialInventory credentials,
        IRepoModelPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(candidateModels);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(policy);

        var options = new List<AdapterModelOption>();
        var exclusions = new List<AdapterModelExclusion>();

        // Credential kind -> the first usable credential of that kind, in the order the caller gave
        // them, which is the order section 20b.3 resolved them in.
        var byKind = new Dictionary<string, AvailableCredential>(StringComparer.Ordinal);
        foreach (var credential in credentials.AvailableCredentials)
        {
            _ = byKind.TryAdd(credential.Kind, credential);
        }

        foreach (var adapter in adapters)
        {
            var adapterPermitted = policy.AllowedAdapterIds.Count == 0
                || policy.AllowedAdapterIds.Any(pattern => Matches(pattern, adapter.Id));

            foreach (var candidate in candidateModels)
            {
                var model = ModelIdentifier.Parse(candidate);
                var display = candidate;

                if (!adapterPermitted)
                {
                    exclusions.Add(new AdapterModelExclusion(
                        adapter.Id,
                        display,
                        AdapterModelExclusionReason.AdapterNotPermitted,
                        $"the repository does not permit the '{adapter.Id}' adapter."));
                    continue;
                }

                if (policy.DeniedModels.Any(pattern => Matches(pattern, display)))
                {
                    exclusions.Add(new AdapterModelExclusion(
                        adapter.Id,
                        display,
                        AdapterModelExclusionReason.ModelDenied,
                        $"the repository denies the model '{display}'."));
                    continue;
                }

                if (policy.AllowedModels.Count > 0
                    && !policy.AllowedModels.Any(pattern => Matches(pattern, display)))
                {
                    exclusions.Add(new AdapterModelExclusion(
                        adapter.Id,
                        display,
                        AdapterModelExclusionReason.ModelNotAllowed,
                        $"the repository's allowed model list does not include '{display}'."));
                    continue;
                }

                var kindsForProvider = adapter.Auth
                    .Where(binding => string.Equals(
                        AdapterCredentialKinds.ProviderFor(binding.CredentialKind),
                        model.Provider,
                        StringComparison.Ordinal))
                    .Select(binding => binding.CredentialKind)
                    .ToList();

                if (kindsForProvider.Count == 0)
                {
                    exclusions.Add(new AdapterModelExclusion(
                        adapter.Id,
                        display,
                        AdapterModelExclusionReason.AdapterCannotUseProvider,
                        $"the '{adapter.Id}' adapter cannot authenticate against the '{model.Provider}' "
                        + $"provider, so it cannot build with '{display}'"
                        + Alternatives(adapters, adapter.Id, model.Provider)));
                    continue;
                }

                var match = kindsForProvider.FirstOrDefault(byKind.ContainsKey);
                if (match is null)
                {
                    exclusions.Add(new AdapterModelExclusion(
                        adapter.Id,
                        display,
                        AdapterModelExclusionReason.NoCredential,
                        $"no usable credential of kind {string.Join(" or ", kindsForProvider)} is available "
                        + $"for '{display}'."));
                    continue;
                }

                var credential = byKind[match];
                options.Add(new AdapterModelOption(
                    adapter.Id,
                    display,
                    model.Provider,
                    match,
                    credential.Endpoint));
            }
        }

        return new AdapterCompatibility(options, exclusions);
    }

    /// <summary>
    /// Names the loaded adapters that <em>can</em> reach a provider, so a refusal points somewhere.
    /// </summary>
    /// <remarks>
    /// This is the difference between "Claude Code cannot use OpenRouter" and "Claude Code cannot use
    /// OpenRouter; pi can". The first is a dead end for the requester, the second is the Phase 1
    /// answer: Claude Code authenticates against the Anthropic API or a gateway presenting it, and pi
    /// is provider-agnostic and reaches an aggregator natively.
    /// </remarks>
    private static string Alternatives(
        IReadOnlyList<AdapterDocument> adapters,
        string excludedAdapterId,
        string provider)
    {
        var able = adapters
            .Where(candidate => !string.Equals(candidate.Id, excludedAdapterId, StringComparison.Ordinal))
            .Where(candidate => candidate.Auth.Any(binding => string.Equals(
                AdapterCredentialKinds.ProviderFor(binding.CredentialKind),
                provider,
                StringComparison.Ordinal)))
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return able.Count == 0
            ? "."
            : $". {string.Join(" and ", able.Select(id => $"'{id}'"))} can.";
    }

    /// <summary>Ordinal match, with an optional trailing <c>*</c>.</summary>
    private static bool Matches(string pattern, string value)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (pattern.EndsWith('*'))
        {
            return value.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        return string.Equals(pattern, value, StringComparison.Ordinal);
    }
}
