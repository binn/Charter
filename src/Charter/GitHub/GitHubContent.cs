namespace Charter.GitHub;

/// <summary>One file read out of a repository at a ref.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Sha">The blob SHA, which is what makes a per-commit cache correct.</param>
/// <param name="Text">Decoded UTF-8 content.</param>
public sealed record GitHubFile(string Path, string Sha, string Text);

/// <summary>One entry in a git tree listing.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Type"><c>blob</c>, <c>tree</c> or <c>commit</c> (a submodule).</param>
/// <param name="Sha">The object SHA.</param>
/// <param name="Size">Blob size in bytes, when GitHub reported one.</param>
public sealed record GitHubTreeEntry(string Path, string Type, string Sha, long? Size)
{
    /// <summary>Whether this entry is a file rather than a directory or a submodule.</summary>
    public bool IsBlob => string.Equals(Type, "blob", StringComparison.Ordinal);
}

/// <summary>A file to write in a commit.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Text">The whole new content of the file.</param>
public sealed record GitHubFileEdit(string Path, string Text);

/// <summary>The commit a write produced.</summary>
/// <param name="Sha">The new commit SHA.</param>
/// <param name="Branch">The branch it now points at.</param>
public sealed record GitHubCommitResult(string Sha, string Branch);

/// <summary>A pull request Charter opened. Charter never merges one (section 7.4).</summary>
/// <param name="Number">The PR number.</param>
/// <param name="Url">The <c>html_url</c>, for the engineer.</param>
/// <param name="HeadSha">The head commit.</param>
/// <param name="HeadBranch">The branch the PR is from.</param>
public sealed record GitHubPullRequestResult(int Number, string Url, string HeadSha, string HeadBranch);

/// <summary>What GitHub currently says about a pull request.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="Url">The <c>html_url</c>.</param>
/// <param name="State"><c>open</c> or <c>closed</c>, as GitHub spells it.</param>
/// <param name="Merged">Whether a closed pull request was merged rather than abandoned.</param>
/// <param name="Draft">Whether it is a draft.</param>
/// <param name="HeadSha">The head commit.</param>
/// <param name="HeadBranch">The branch it is from.</param>
/// <param name="BaseBranch">The branch it is aimed at.</param>
/// <param name="Labels">Its labels, by name.</param>
public sealed record GitHubPullRequestDetail(
    int Number,
    string Url,
    string State,
    bool Merged,
    bool Draft,
    string HeadSha,
    string? HeadBranch,
    string? BaseBranch,
    IReadOnlyList<string> Labels);

/// <summary>Two revisions compared. Section 17 needs both halves of this.</summary>
/// <param name="AheadBy">Commits the head has that the base does not.</param>
/// <param name="BehindBy">Commits the base has that the head does not.</param>
/// <param name="Files">Repository-relative paths that differ.</param>
public sealed record GitHubComparison(int AheadBy, int BehindBy, IReadOnlyList<string> Files);

/// <summary>
/// What GitHub reports about a branch protection rule (change spec 001 part A.5).
/// </summary>
/// <param name="Protected">Whether a rule exists at all.</param>
/// <param name="RequiredApprovals">How many approving reviews a merge needs, when reviews are required.</param>
/// <param name="RequiresCodeOwnerReview">Whether <c>CODEOWNERS</c> must sign off.</param>
/// <param name="DismissesStaleReviews">Whether new commits dismiss existing approvals.</param>
/// <param name="EnforcedForAdministrators">Whether administrators are bound by the rule too.</param>
/// <param name="Detail">One line, safe to show an engineer. Never a raw API body.</param>
public sealed record GitHubBranchProtection(
    bool Protected,
    int? RequiredApprovals = null,
    bool RequiresCodeOwnerReview = false,
    bool DismissesStaleReviews = false,
    bool EnforcedForAdministrators = false,
    string? Detail = null);

/// <summary>A repository webhook.</summary>
/// <param name="Id">GitHub's id for it.</param>
/// <param name="Url">Where it delivers.</param>
/// <param name="Created">False when a hook for the same URL already existed.</param>
public sealed record GitHubWebhookHook(long Id, string Url, bool Created);

/// <summary>The handful of repository facts Charter reads back after creating or transferring one.</summary>
/// <param name="FullName"><c>owner/name</c>.</param>
/// <param name="DefaultBranch">Its default branch.</param>
/// <param name="Private">Whether it is private.</param>
public sealed record GitHubRepositorySummary(string FullName, string DefaultBranch, bool Private);
