namespace Charter.VersionControl;

/// <summary>
/// Whether the provider can <em>enforce</em> that agent-written code is reviewed before it merges
/// (change spec 001 part A.5).
/// </summary>
/// <remarks>
/// Specification section 7.4 gives the merge gate to provider-side branch protection, outside
/// Charter's trust boundary. That guarantee is only as strong as the provider makes it, so it is
/// recorded rather than assumed.
/// </remarks>
public enum MergeGateEnforcement
{
    /// <summary>
    /// The provider refuses the merge itself. Section 7.4's guarantee holds unchanged.
    /// </summary>
    ProviderEnforced,

    /// <summary>
    /// Nothing stops a person merging agent-written code without review. Charter will not do it and
    /// Charter cannot prevent it either, so it says so loudly instead of implying otherwise.
    /// </summary>
    Advisory,
}

/// <summary>
/// What a provider can actually do (change spec 001 part A.3).
/// </summary>
/// <remarks>
/// <para>
/// Providers differ enormously, and Charter degrades explicitly rather than assuming. Every value
/// here is a statement about the provider's API, not about one repository — whether a <em>given</em>
/// repository has protection configured is <see cref="MergeGateAssessment"/>'s question, and part
/// A.5 is emphatic that supporting protection and having it configured are not the same fact.
/// </para>
/// <para>
/// Two fields extend part A.3's list, because two things Charter already promises need them:
/// <see cref="ChangeRequestComments"/> (section 14 posts the engineer recap as a provider comment
/// "where supported", in-app where not) and <see cref="ChangeRequestLabels"/> (section 7.5's
/// <c>unreviewed-spec</c> and section 15's <c>schema-change</c>). A provider that has neither still
/// works; the recap and the labels move into the session view.
/// </para>
/// </remarks>
public sealed record VersionControlCapabilities
{
    /// <summary>The provider has a code-review surface at all. False for a bare git remote (part A.6).</summary>
    public required bool ChangeRequests { get; init; }

    /// <summary>Inbound delivery of repository events.</summary>
    public required bool Webhooks { get; init; }

    /// <summary>Scoped installation-style tokens rather than a long-lived personal access token.</summary>
    public required bool AppStyleAuth { get; init; }

    public required bool BranchProtection { get; init; }

    /// <summary>A <c>CODEOWNERS</c> file the provider honours. On GitLab this is a paid tier.</summary>
    public required bool CodeOwners { get; init; }

    public required bool RepoCreation { get; init; }

    public required bool RepoTransfer { get; init; }

    /// <summary>Can trigger provider-native CI. Gates <c>CiDispatchRunner</c> (part A.8).</summary>
    public required bool CiDispatch { get; init; }

    /// <summary>Section 7.4's merge gate, as far as this provider can carry it.</summary>
    public required MergeGateEnforcement MergeGateEnforcement { get; init; }

    /// <summary>Comments on a change request. Section 14's recap lands here where it is true.</summary>
    public bool ChangeRequestComments { get; init; }

    /// <summary>Labels on a change request. Sections 7.5 and 15 both apply one.</summary>
    public bool ChangeRequestLabels { get; init; }

    /// <summary>Whether the provider can report the files a change request touches (section 17).</summary>
    public bool ChangedFileListing { get; init; }

    /// <summary>Everything off, and advisory. The honest starting point for a new provider.</summary>
    public static VersionControlCapabilities None { get; } = new()
    {
        ChangeRequests = false,
        Webhooks = false,
        AppStyleAuth = false,
        BranchProtection = false,
        CodeOwners = false,
        RepoCreation = false,
        RepoTransfer = false,
        CiDispatch = false,
        MergeGateEnforcement = MergeGateEnforcement.Advisory,
    };
}

/// <summary>
/// The words a provider uses, so the UI reads correctly (change spec 001 part A.2).
/// </summary>
/// <param name="ChangeRequest">Singular, lower case: <c>pull request</c>, <c>merge request</c>.</param>
/// <param name="ChangeRequestPlural">Plural, lower case.</param>
/// <param name="ChangeRequestShort">The short form an engineer types: <c>PR</c>, <c>MR</c>, <c>CL</c>.</param>
/// <param name="Branch">What a line of work is called: <c>branch</c>, <c>stream</c>.</param>
public sealed record VersionControlTerms(
    string ChangeRequest,
    string ChangeRequestPlural,
    string ChangeRequestShort,
    string Branch = "branch")
{
    /// <summary>GitHub, Gitea, Bitbucket and Azure DevOps all say "pull request".</summary>
    public static VersionControlTerms PullRequest { get; } = new("pull request", "pull requests", "PR");

    /// <summary>GitLab.</summary>
    public static VersionControlTerms MergeRequest { get; } = new("merge request", "merge requests", "MR");

    /// <summary>The neutral fallback, for a provider with no review surface (part A.6).</summary>
    public static VersionControlTerms ChangeRequestDefault { get; } =
        new("change request", "change requests", "CR");
}
