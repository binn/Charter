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
