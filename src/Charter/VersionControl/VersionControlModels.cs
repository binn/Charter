using Charter.Configuration;
using Charter.Domain;

namespace Charter.VersionControl;

/// <summary>
/// One repository, provider-agnostically.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProviderId"/> says which provider owns it, <see cref="Path"/> is whatever that
/// provider calls a repository — <c>owner/name</c> on GitHub, <c>group/subgroup/project</c> on
/// GitLab, a depot path on Perforce — and <see cref="InstallationId"/> is the installation, project
/// access token or workspace binding through which Charter reaches it.
/// </para>
/// <para>
/// Nothing here is a URL. A provider builds its own URLs; a caller that had one would be tempted to
/// parse it.
/// </para>
/// </remarks>
public sealed record RepoRef
{
    public required string ProviderId { get; init; }

    /// <summary>The provider's own name for the repository. Slashes are allowed and meaningful.</summary>
    public required string Path { get; init; }

    /// <summary>The binding Charter authenticates through, where the provider has one.</summary>
    public long? InstallationId { get; init; }

    /// <summary>The branch change requests are opened against.</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>Charter's own row, when this reference came from one.</summary>
    public Guid? RepoId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{ProviderId}:{Path}";

    /// <summary>The reference for a connected <see cref="Repo"/> row.</summary>
    public static RepoRef For(Repo repo, string providerId)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new RepoRef
        {
            ProviderId = providerId,
            Path = repo.FullName,
            InstallationId = repo.GithubInstallationId,
            BaseBranch = repo.BaseBranch,
            RepoId = repo.Id,
        };
    }
}

/// <summary>How much a minted credential is allowed to do (section 7.4).</summary>
public enum VersionControlAccess
{
    /// <summary>Reads code and metadata. What a recon run gets.</summary>
    Read,

    /// <summary>Writes a branch and opens a change request. It cannot merge, ever.</summary>
    Contribute,

    /// <summary>
    /// Administers the repository. Only the optional operations of part A.2 ask for it, and only when
    /// the capability that gates them is on.
    /// </summary>
    Administer,
}

/// <summary>
/// A scoped, short-TTL credential for exactly one repository (section 7.4, part A.2).
/// </summary>
/// <remarks>
/// It carries a value and no way to mint another. Anything downstream that wants a fresh credential
/// has to come back through the control plane and be audited for it, which is the whole point.
/// </remarks>
public sealed record VersionControlCredential
{
    /// <summary>The single repository this credential reaches.</summary>
    public required string Repository { get; init; }

    /// <summary>The secret itself. Never logged, never returned to the UI.</summary>
    public required Secret Token { get; init; }

    /// <summary>When it stops working. There is no renewal path from here.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>What it was minted for.</summary>
    public required VersionControlAccess Access { get; init; }

    /// <summary>
    /// The username half, for providers whose git transport wants one (<c>oauth2</c> on GitLab,
    /// <c>x-access-token</c> on GitHub). Null where the token stands alone.
    /// </summary>
    public string? Username { get; init; }

    /// <inheritdoc />
    public override string ToString()
        => $"{Access} credential for {Repository}, expires {ExpiresAt:O}";
}

/// <summary>
/// Everything a runner needs to materialise a working copy, and nothing the control plane had to
/// clone to produce.
/// </summary>
/// <remarks>
/// Part A.2 lists clone / fetch / worktree behind the seam. The control plane never holds a working
/// copy — it runs on a PaaS with an ephemeral filesystem (section 2.3) — so what it returns is the
/// <em>instruction</em> to check out, which the execution plane carries out. A provider with no
/// cheap branching (part A.7) answers with a workspace specification instead, and nothing above this
/// type has to change.
/// </remarks>
public sealed record WorkspaceCheckout
{
    /// <summary>Where to fetch from. Already carries whatever the provider's transport needs.</summary>
    public required Uri RemoteUrl { get; init; }

    /// <summary>The credential to fetch with.</summary>
    public required VersionControlCredential Credential { get; init; }

    /// <summary>The revision to start from — a commit, a tag, or a branch head.</summary>
    public required string Revision { get; init; }

    /// <summary>The branch to create for the session's work, where the provider has branches.</summary>
    public string? WorkingBranch { get; init; }

    /// <summary>How deep to fetch. Null means the whole history.</summary>
    public int? Depth { get; init; }
}

/// <summary>What a push moved a ref to.</summary>
/// <param name="Reference">The ref that now points at <paramref name="Revision"/>.</param>
/// <param name="Revision">The commit, changelist or equivalent.</param>
/// <param name="Created">True when the ref did not exist before.</param>
public sealed record PushResult(string Reference, string Revision, bool Created);

/// <summary>One change request, by the identity its provider gives it.</summary>
/// <param name="Repo">The repository it belongs to.</param>
/// <param name="Number">
/// The provider's own number. An integer because every provider Charter targets in this change spec
/// numbers them; a provider that does not would carry the identity in <paramref name="ExternalId"/>.
/// </param>
/// <param name="ExternalId">The provider's opaque id, where it differs from the number.</param>
public sealed record ChangeRequestRef(RepoRef Repo, int Number, string? ExternalId = null);

/// <summary>
/// What the provider says about a change request right now.
/// </summary>
/// <remarks>
/// <see cref="SourceBranch"/> is nullable on purpose. Part A.7: do not assume a change request is a
/// branch — a shelved changelist has no branch behind it, and a snapshot that required one would
/// have foreclosed Perforce in the type system.
/// </remarks>
public sealed record ChangeRequestSnapshot
{
    public required int Number { get; init; }

    public required string Url { get; init; }

    public required ChangeRequestState State { get; init; }

    /// <summary>The head commit, changelist, or equivalent revision.</summary>
    public required string HeadRevision { get; init; }

    /// <summary>The branch the work is on, where the provider has one.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>The branch it is aimed at.</summary>
    public string? TargetBranch { get; init; }

    /// <summary>
    /// Who the provider records as the author, in its own namespace.
    /// </summary>
    /// <remarks>
    /// Carried for section 18: a preview platform that will not deploy a branch from an account
    /// outside its workspace can only report that as absence, and the account's name is the whole
    /// difference between an actionable warning and "the preview is taking a while".
    /// </remarks>
    public string? AuthorLogin { get; init; }

    /// <summary>Labels the provider reports, where it has labels.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
}

/// <summary>
/// What to open, stated without assuming the provider has branches.
/// </summary>
/// <param name="Repo">The repository.</param>
/// <param name="Source">The branch, shelf or revision carrying the work.</param>
/// <param name="Target">The branch it is aimed at — the repo's base branch, normally.</param>
/// <param name="Title">One line.</param>
/// <param name="BodyMarkdown">The description. Markdown where the provider renders it.</param>
/// <param name="Labels">
/// Applied where <see cref="VersionControlCapabilities.ChangeRequestLabels"/> is true. Sections 7.5
/// and 15 both depend on one arriving.
/// </param>
/// <param name="Draft">Opened as a draft where the provider has the concept.</param>
public sealed record OpenChangeRequestCommand(
    RepoRef Repo,
    string Source,
    string Target,
    string Title,
    string BodyMarkdown,
    IReadOnlyList<string>? Labels = null,
    bool Draft = false);

/// <summary>How two revisions relate, which is half of section 17's staleness test.</summary>
/// <param name="BehindBy">Commits on the base that the head does not have. Zero means up to date.</param>
/// <param name="AheadBy">Commits on the head that the base does not have.</param>
/// <param name="ChangedFiles">Repository-relative paths that differ.</param>
public sealed record RevisionComparison(int BehindBy, int AheadBy, IReadOnlyList<string> ChangedFiles);

/// <summary>
/// Whether a specific branch of a specific repository is actually protected (part A.5).
/// </summary>
/// <param name="Configured">
/// True only when the provider reports a rule that genuinely stands between a person and a merge. A
/// repository with no rule is functionally advisory however capable its provider is.
/// </param>
/// <param name="RequiresReview">A review is required before merge.</param>
/// <param name="RequiredApprovals">How many, where the provider reports a count.</param>
/// <param name="CodeOwnersReviewRequired">A <c>CODEOWNERS</c> review is required.</param>
/// <param name="DismissesStaleReviews">Approvals are dismissed when new commits arrive.</param>
/// <param name="EnforcedForAdministrators">
/// Administrators are subject to the rule too. Where they are not, "protected" means "protected from
/// everybody who could not simply turn it off", which is worth knowing.
/// </param>
/// <param name="Detail">One line, safe to show an engineer. Never a raw API body.</param>
public sealed record BranchProtectionStatus(
    bool Configured,
    bool RequiresReview = false,
    int? RequiredApprovals = null,
    bool CodeOwnersReviewRequired = false,
    bool DismissesStaleReviews = false,
    bool EnforcedForAdministrators = false,
    string? Detail = null)
{
    /// <summary>The answer for a provider that cannot protect a branch at all.</summary>
    public static BranchProtectionStatus Unsupported { get; } =
        new(false, Detail: "this provider cannot enforce branch protection");

    /// <summary>The answer when the provider could be asked but had nothing to report.</summary>
    public static BranchProtectionStatus None { get; } =
        new(false, Detail: "no branch protection rule covers this branch");
}

/// <summary>What to protect, and how (part A.2's optional <c>ApplyBranchProtection</c>).</summary>
/// <param name="Branch">The branch to protect.</param>
/// <param name="RequiredApprovals">How many reviews a merge needs.</param>
/// <param name="RequireCodeOwnerReview">Whether <c>CODEOWNERS</c> must sign off.</param>
/// <param name="DismissStaleReviews">Whether new commits dismiss existing approvals.</param>
/// <param name="EnforceForAdministrators">Whether administrators are bound by it too.</param>
public sealed record BranchProtectionRequest(
    string Branch,
    int RequiredApprovals = 1,
    bool RequireCodeOwnerReview = true,
    bool DismissStaleReviews = true,
    bool EnforceForAdministrators = false);

/// <summary>The events Charter wants delivered.</summary>
/// <param name="CallbackUrl">Where the provider posts.</param>
/// <param name="Secret">The shared secret the delivery is signed with.</param>
/// <param name="Events">
/// Provider-neutral names: <c>push</c>, <c>change_request</c>, <c>change_request_review</c>,
/// <c>check_suite</c>, <c>installation</c>. The provider translates.
/// </param>
public sealed record WebhookSubscription(Uri CallbackUrl, Secret Secret, IReadOnlyList<string> Events);

/// <summary>The webhook the provider now has.</summary>
/// <param name="ExternalId">The provider's id for it, for a later update or delete.</param>
/// <param name="CallbackUrl">Where it points.</param>
/// <param name="Created">False when a matching webhook already existed.</param>
public sealed record WebhookRegistration(string ExternalId, Uri CallbackUrl, bool Created);

/// <summary>A repository to create (part A.2's optional <c>CreateRepository</c>).</summary>
/// <param name="Owner">The organisation or user that will own it.</param>
/// <param name="Name">The repository name.</param>
/// <param name="Private">Whether it is private. Charter never creates a public repository by default.</param>
/// <param name="Description">One line.</param>
/// <param name="InstallationId">
/// The binding to create it through, where the provider needs one named. A repository that does not
/// exist yet cannot be authenticated to, so the caller says which installation, project or workspace
/// is doing the creating.
/// </param>
public sealed record NewRepositoryRequest(
    string Owner,
    string Name,
    bool Private = true,
    string? Description = null,
    long? InstallationId = null);

/// <summary>
/// The caller asked a provider for something it does not do.
/// </summary>
/// <remarks>
/// Thrown rather than returned so an unguarded call fails loudly in a test rather than silently
/// degrading in production. Every optional operation is gated by a capability, and the capability is
/// the thing callers are expected to check.
/// </remarks>
public sealed class VersionControlCapabilityException : Exception
{
    public VersionControlCapabilityException(string providerId, string operation)
        : base($"The {providerId} provider does not support {operation}.")
    {
        ProviderId = providerId;
        Operation = operation;
    }

    public VersionControlCapabilityException(string message)
        : base(message)
    {
    }

    public VersionControlCapabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VersionControlCapabilityException()
        : base("The version control provider does not support that operation.")
    {
    }

    public string? ProviderId { get; }

    public string? Operation { get; }
}
