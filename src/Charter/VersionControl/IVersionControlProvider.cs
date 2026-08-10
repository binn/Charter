namespace Charter.VersionControl;

/// <summary>
/// Every version-control operation Charter performs, behind one seam (change spec 001 part A.2).
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 ships exactly one implementation — GitHub — and the interface exists anyway, because
/// retrofitting a seam is expensive and because the shape of the seam is what decides whether the
/// second provider is a week or a quarter. Two traps are designed out rather than commented on:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>A change request is not a branch.</strong> <see cref="OpenChangeRequestAsync"/> takes a
/// <em>source</em>, which is a branch on GitHub and a shelved changelist on Perforce, and
/// <see cref="ChangeRequestSnapshot.SourceBranch"/> is nullable. Nothing above this interface may
/// assume it can check one out.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>The provider may not be able to enforce review.</strong>
/// <see cref="VersionControlCapabilities.MergeGateEnforcement"/> is a declaration, and
/// <see cref="GetBranchProtectionAsync"/> is how Charter checks whether a specific repository has
/// actually configured it. Part A.5 turns on the difference between the two.
/// </description>
/// </item>
/// </list>
/// <para>
/// There is no merge call, and there never will be one. Section 7.4 gives the merge gate to the
/// provider, and an interface that cannot express a merge is one fewer thing to get wrong.
/// </para>
/// </remarks>
public interface IVersionControlProvider
{
    /// <summary>Stable, lower case: <c>github</c>, <c>gitlab</c>, <c>gitea</c>.</summary>
    string Id { get; }

    /// <summary>The provider's own name, for an operator to read.</summary>
    string DisplayName { get; }

    /// <summary>What this provider can do (part A.3).</summary>
    VersionControlCapabilities Capabilities { get; }

    /// <summary>The words the UI uses for this provider (part A.2).</summary>
    VersionControlTerms Terms { get; }

    /// <summary>Mints a scoped, short-TTL credential for one repository (section 7.4).</summary>
    Task<VersionControlCredential> AuthenticateRepoAsync(
        RepoRef repo,
        VersionControlAccess access = VersionControlAccess.Read,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the checkout a runner should make: where to fetch from, with what, at which revision.
    /// </summary>
    /// <remarks>
    /// The control plane never holds a working copy (section 2.3), so clone, fetch and worktree are
    /// expressed as an instruction the execution plane carries out rather than as work done here.
    /// </remarks>
    Task<WorkspaceCheckout> PrepareWorkspaceAsync(
        RepoRef repo,
        string revision,
        string? workingBranch = null,
        CancellationToken cancellationToken = default);

    /// <summary>The revision a branch points at, or <see langword="null"/> when there is no such branch.</summary>
    Task<string?> GetBranchHeadAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a branch at a revision. Fails if it already exists.</summary>
    Task CreateBranchAsync(
        RepoRef repo,
        string branch,
        string fromRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a remote ref to a revision the provider already holds.
    /// </summary>
    /// <remarks>
    /// The bytes are pushed by whoever has the working copy — the runner. What the control plane does
    /// is publish the ref, which is the half that needs a credential it is not willing to hand out.
    /// </remarks>
    Task<PushResult> PushAsync(
        RepoRef repo,
        string branch,
        string revision,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a change request. Charter never merges one (section 7.4).</summary>
    Task<ChangeRequestSnapshot> OpenChangeRequestAsync(
        OpenChangeRequestCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Comments on a change request. Section 14's engineer recap lands here.
    /// </summary>
    /// <returns>
    /// True when the comment was posted; false when the provider has no comment surface, so the
    /// caller can fall back to the session view rather than losing the recap.
    /// </returns>
    Task<bool> CommentOnChangeRequestAsync(
        ChangeRequestRef changeRequest,
        string bodyMarkdown,
        CancellationToken cancellationToken = default);

    /// <summary>The provider's current view of a change request, or null when it is gone.</summary>
    Task<ChangeRequestSnapshot?> GetChangeRequestStateAsync(
        ChangeRequestRef changeRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies labels, where the provider has them. Sections 7.5 and 15 both depend on this.
    /// </summary>
    /// <returns>False when the provider has no labels, so the caller can say so in the body instead.</returns>
    Task<bool> LabelChangeRequestAsync(
        ChangeRequestRef changeRequest,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How a revision relates to the base branch, and which files differ (section 17).
    /// </summary>
    Task<RevisionComparison> CompareAsync(
        RepoRef repo,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a webhook, idempotently.</summary>
    Task<WebhookRegistration> RegisterWebhookAsync(
        RepoRef repo,
        WebhookSubscription subscription,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this branch of this repository is actually protected (part A.5).
    /// </summary>
    /// <remarks>
    /// Supported and configured are different facts, and onboarding (section 9) checks the second.
    /// A provider that cannot protect a branch answers <see cref="BranchProtectionStatus.Unsupported"/>
    /// rather than throwing: "no" is an answer, and every caller wants it.
    /// </remarks>
    Task<BranchProtectionStatus> GetBranchProtectionAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional (section 26.10). Throws <see cref="VersionControlCapabilityException"/> when
    /// <see cref="VersionControlCapabilities.RepoCreation"/> is false.
    /// </summary>
    Task<RepoRef> CreateRepositoryAsync(
        NewRepositoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional (section 26.9). Throws <see cref="VersionControlCapabilityException"/> when
    /// <see cref="VersionControlCapabilities.RepoTransfer"/> is false.
    /// </summary>
    Task<RepoRef> TransferRepositoryAsync(
        RepoRef repo,
        string newOwner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional. Throws <see cref="VersionControlCapabilityException"/> when
    /// <see cref="VersionControlCapabilities.BranchProtection"/> is false.
    /// </summary>
    Task ApplyBranchProtectionAsync(
        RepoRef repo,
        BranchProtectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the provider a repository belongs to.
/// </summary>
/// <remarks>
/// Phase 1 has one provider, and this still exists, because the alternative is that every call site
/// injects <c>GitHubVersionControlProvider</c> and the seam is decorative. A repository names its
/// provider; the registry hands back the implementation.
/// </remarks>
public interface IVersionControlProviderRegistry
{
    /// <summary>Every registered provider.</summary>
    IReadOnlyList<IVersionControlProvider> Providers { get; }

    /// <summary>The provider with this id, or null.</summary>
    IVersionControlProvider? Find(string providerId);

    /// <summary>The provider for a connected repository row.</summary>
    IVersionControlProvider For(Charter.Domain.Repo repo);

    /// <summary>The reference this repository is reached by.</summary>
    RepoRef ReferenceFor(Charter.Domain.Repo repo);

    /// <summary>
    /// The words to use for this repository, for a read path that must not fail (part A.2).
    /// </summary>
    /// <remarks>
    /// Never throws. A projection that could not render a request because no provider was registered
    /// would be a display concern taking down a read, so an instance with no provider falls back to
    /// Charter's own neutral wording.
    /// </remarks>
    VersionControlTerms TermsFor(Charter.Domain.Repo repo);
}
