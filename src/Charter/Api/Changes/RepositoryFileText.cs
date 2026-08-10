using System.Text;
using Charter.Domain;
using Charter.GitHub;

namespace Charter.Api.Changes;

/// <summary>
/// Reads one file out of a repository at one revision, for pane 3.
/// </summary>
/// <remarks>
/// <para>
/// Pane 3 needs the file's text on both sides of the change, and Charter holds neither: section 2.3
/// forbids a working copy on the control plane, and the transcript records that a write happened
/// rather than what the file became. So the text is fetched from the provider, per file, when
/// somebody opens one.
/// </para>
/// <para>
/// This is a seam rather than a direct call because <c>IVersionControlProvider</c> has no content
/// read — it deals in checkouts, refs and change requests. Adding one is a change to a file this work
/// does not own; see the report. Until then a GitHub-backed implementation is the only one, and an
/// instance with no GitHub wiring answers <see cref="FileTextUnavailable"/> rather than throwing.
/// </para>
/// </remarks>
public interface IRepositoryFileText
{
    /// <summary>
    /// The file at a revision, or <see langword="null"/> when it does not exist there — which is how
    /// an added file's "before" and a deleted file's "after" both read.
    /// </summary>
    /// <exception cref="FileTextUnavailableException">Nothing on this instance can read the file.</exception>
    Task<FileText?> ReadAsync(
        Repo repo,
        string revision,
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>There is no way to read repository content on this instance.</summary>
public sealed class FileTextUnavailableException : Exception
{
    private const string Sentence =
        "Charter cannot read this file's contents on this instance, so there is no before-and-after "
        + "to show. Open the change request to read the diff.";

    /// <summary>Creates the exception.</summary>
    public FileTextUnavailableException()
        : base(Sentence)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FileTextUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FileTextUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The GitHub-backed reader, tolerating a host that wired no GitHub client.
/// </summary>
/// <remarks>
/// The dependency is an <see cref="IEnumerable{T}"/> on purpose: the container always resolves one,
/// empty when nothing registered <see cref="IGitHubRepositoryClient"/>. That keeps
/// <c>AddCharterApi()</c> resolvable in a host that has not called <c>AddCharterGitHub()</c> without
/// this type reaching into the service provider to find out.
/// </remarks>
public sealed class GitHubRepositoryFileText : IRepositoryFileText
{
    /// <summary>
    /// The most text pane 3 will carry for one side of one file.
    /// </summary>
    /// <remarks>
    /// A quarter of a megabyte is a very large source file and a small fraction of a minified bundle
    /// or a checked-in fixture. Past it the text is cut and <see cref="FileText.Truncated"/> says so,
    /// because section 27.4's rule — do not pretend parity — applies to a diff too.
    /// </remarks>
    public const int MaxBytes = 256 * 1024;

    private readonly IGitHubRepositoryClient? client;

    public GitHubRepositoryFileText(IEnumerable<IGitHubRepositoryClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        client = clients.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<FileText?> ReadAsync(
        Repo repo,
        string revision,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (client is null)
        {
            throw new FileTextUnavailableException();
        }

        var repository = GitHubRepository.Parse(repo.FullName, repo.GithubInstallationId);
        var file = await client.GetFileAsync(repository, path, revision, cancellationToken);

        return file is null ? null : Classify(file.Text);
    }

    /// <summary>
    /// Decides whether what came back is text at all, and cuts it if it is enormous.
    /// </summary>
    /// <remarks>
    /// A NUL byte is the same test <c>git</c> uses, and it is the right one here: a PNG or a
    /// compiled asset must render as <em>"there is nothing to show"</em> rather than as a screenful
    /// of replacement characters that looks like a corrupted file.
    /// </remarks>
    public static FileText Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return new FileText(string.Empty, Binary: true);
        }

        if (Encoding.UTF8.GetByteCount(text) <= MaxBytes)
        {
            return new FileText(text);
        }

        // Cut on a line boundary where there is one nearby, so the last line shown is a whole line.
        var prefix = text[..Math.Min(text.Length, MaxBytes)];
        var lastNewline = prefix.LastIndexOf('\n');

        return new FileText(
            lastNewline > MaxBytes / 2 ? prefix[..(lastNewline + 1)] : prefix,
            Binary: false,
            Truncated: true);
    }
}
