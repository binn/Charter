using Charter.Domain;
using Microsoft.Extensions.Logging;

namespace Charter.VersionControl;

/// <summary>
/// What the merge gate is worth for one repository, right now (change spec 001 part A.5).
/// </summary>
/// <param name="ProviderId">Which provider was asked.</param>
/// <param name="Repository">The repository, as the provider names it.</param>
/// <param name="Branch">The branch that was checked — the base branch.</param>
/// <param name="Declared">What the provider can do at all.</param>
/// <param name="Effective">
/// What is true of <em>this</em> repository. A GitHub repository with no branch protection rule is
/// <see cref="MergeGateEnforcement.Advisory"/> however capable GitHub is, which is the entire point
/// of part A.5's onboarding step.
/// </param>
/// <param name="Protection">What the provider reported about the branch.</param>
public sealed record MergeGateAssessment(
    string ProviderId,
    string Repository,
    string Branch,
    MergeGateEnforcement Declared,
    MergeGateEnforcement Effective,
    BranchProtectionStatus Protection)
{
    /// <summary>True when section 7.4's v1 guarantee holds unchanged for this repository.</summary>
    public bool IsEnforced => Effective == MergeGateEnforcement.ProviderEnforced;

    /// <summary>
    /// Why it is advisory, in the plain wording part A.5 asks for. Null when it is enforced.
    /// </summary>
    /// <remarks>
    /// The wording is deliberately flat and deliberately not reassuring. An operator who reads this
    /// and shrugs has made an informed choice; an operator who was never told has been misled about
    /// the strongest property Charter claims.
    /// </remarks>
    public string? Warning => Effective == MergeGateEnforcement.ProviderEnforced
        ? null
        : Declared == MergeGateEnforcement.Advisory
            ? $"{ProviderId} cannot enforce review. Nothing stops a person from merging agent-written "
              + "code without review. Charter will not do it, but Charter cannot prevent it either."
            : $"No branch protection rule covers '{Branch}' in {Repository}, so nothing stops a person "
              + "from merging agent-written code without review. Charter will not do it, but Charter "
              + "cannot prevent it either. Add a rule requiring review before merge to get the "
              + "guarantee Charter's security model describes.";

    /// <summary>The audit-log and settings spelling: <c>provider_enforced</c> or <c>advisory</c>.</summary>
    public string EffectiveName => Effective == MergeGateEnforcement.ProviderEnforced
        ? "provider_enforced"
        : "advisory";
}

/// <summary>
/// Asks a provider whether a repository's merge gate is real (part A.5, amended section 9).
/// </summary>
/// <remarks>
/// Two facts, never conflated: what the provider <em>supports</em>, and what this repository has
/// <em>configured</em>. Onboarding verifies the second, because a repository with no protection rule
/// is functionally advisory and the operator has to be told rather than left to assume the v1
/// guarantee applies.
/// </remarks>
public sealed class MergeGateInspector
{
    private readonly IVersionControlProviderRegistry _registry;
    private readonly ILogger<MergeGateInspector> _logger;

    public MergeGateInspector(IVersionControlProviderRegistry registry, ILogger<MergeGateInspector> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
    }

    /// <summary>Assesses the merge gate for a connected repository's base branch.</summary>
    public async Task<MergeGateAssessment> AssessAsync(Repo repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var provider = _registry.For(repo);
        var reference = _registry.ReferenceFor(repo);

        return await AssessAsync(provider, reference, cancellationToken);
    }

    /// <summary>Assesses the merge gate for one repository on one provider.</summary>
    public async Task<MergeGateAssessment> AssessAsync(
        IVersionControlProvider provider,
        RepoRef repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(repo);

        var declared = provider.Capabilities.MergeGateEnforcement;

        BranchProtectionStatus protection;

        if (!provider.Capabilities.BranchProtection)
        {
            protection = BranchProtectionStatus.Unsupported;
        }
        else
        {
            try
            {
                protection = await provider.GetBranchProtectionAsync(repo, repo.BaseBranch, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Failing closed is the right way round: an unanswered question about the merge gate
                // is reported as "not verified", never as "protected".
                _logger.LogWarning(
                    exception,
                    "Could not read branch protection for {Repository}; treating the merge gate as advisory",
                    repo.Path);

                protection = new BranchProtectionStatus(
                    false,
                    Detail: "Charter could not read the branch protection rule, so it is not verified");
            }
        }

        // Configured, and requiring a review. A rule that only blocks force pushes is a rule; it is
        // not a merge gate, and calling it one would overstate exactly the property section 7.4 rests
        // on.
        var effective = declared == MergeGateEnforcement.ProviderEnforced
                        && protection is { Configured: true, RequiresReview: true }
            ? MergeGateEnforcement.ProviderEnforced
            : MergeGateEnforcement.Advisory;

        var assessment = new MergeGateAssessment(
            provider.Id,
            repo.Path,
            repo.BaseBranch,
            declared,
            effective,
            protection);

        if (!assessment.IsEnforced)
        {
            _logger.LogWarning(
                "The merge gate for {Repository} is advisory: {Warning}",
                repo.Path,
                assessment.Warning);
        }

        return assessment;
    }
}
