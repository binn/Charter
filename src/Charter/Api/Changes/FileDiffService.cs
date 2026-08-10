using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Changes;

/// <summary>How a pane 3 read ended.</summary>
public enum FileDiffReadStatus
{
    Ok,

    /// <summary>No such request, or no such file in this change (section 7.3).</summary>
    NotFound,

    /// <summary>The viewer may not see pane 3 (section 7.4).</summary>
    Forbidden,

    /// <summary>Nothing on this instance can read repository content.</summary>
    Unavailable,
}

/// <summary>One file's diff, with the reason when there is none.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Diff">The file, when there is one.</param>
/// <param name="Reason">Plain language, safe to show (section 11).</param>
public sealed record FileDiffRead(FileDiffReadStatus Status, FileDiffResponse? Diff, string Reason)
{
    /// <summary>Builds the success case.</summary>
    public static FileDiffRead Ok(FileDiffResponse diff) => new(FileDiffReadStatus.Ok, diff, string.Empty);
}

/// <summary>
/// <c>GET /api/requests/{id}/changes/{path}</c> — pane 3, one file at a time (section 12).
/// </summary>
/// <remarks>
/// <para>
/// Per file rather than bundled into <c>RequestDetail</c>, because a session can touch a hundred
/// files and a requester's payload must carry none of them.
/// </para>
/// <para>
/// <strong>The path is checked against the session's own changed files before anything is read.</strong>
/// Without that, this endpoint is a general-purpose repository reader wearing a request id: an
/// engineer has repository read, so the answer would not be a leak — but a path scope (section 7.3)
/// that the runner enforces and the API does not is a scope with a hole in it, and holes get found.
/// </para>
/// <para>
/// Binary and oversized files are reported as such rather than sent as a prefix that looks whole.
/// Section 27.4's <em>do not pretend parity</em> is the same rule in a different pane.
/// </para>
/// </remarks>
public sealed class FileDiffService
{
    private readonly CharterDbContext database;
    private readonly RequestQueryService requests;
    private readonly IRepositoryFileText files;

    public FileDiffService(
        CharterDbContext database,
        RequestQueryService requests,
        IRepositoryFileText files)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(files);

        this.database = database;
        this.requests = requests;
        this.files = files;
    }

    /// <summary>Reads one file's before and after.</summary>
    public async Task<FileDiffRead> ReadAsync(
        MemberSnapshot member,
        Guid requestId,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var wanted = Normalise(path);
        if (wanted.Length == 0)
        {
            return new FileDiffRead(FileDiffReadStatus.NotFound, null, string.Empty);
        }

        var view = await requests.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return new FileDiffRead(FileDiffReadStatus.NotFound, null, string.Empty);
        }

        if (!view.Visibility.Code)
        {
            return new FileDiffRead(
                FileDiffReadStatus.Forbidden,
                null,
                "viewing the code pane needs read access to the repository");
        }

        if (view.Aggregate.Session is not { } session)
        {
            return new FileDiffRead(FileDiffReadStatus.NotFound, null, string.Empty);
        }

        var changed = await ChangedPathsAsync(session.Id, view.Aggregate.Recap, cancellationToken);
        if (!changed.Contains(wanted))
        {
            // Not part of this change. Indistinguishable from "no such file", by design.
            return new FileDiffRead(FileDiffReadStatus.NotFound, null, string.Empty);
        }

        var repo = view.Aggregate.Repo;
        var changeRequest = view.Aggregate.ChangeRequest;

        var head = changeRequest?.HeadSha is { Length: > 0 } sha
            ? sha
            : changeRequest?.HeadBranch ?? repo.BaseBranch;
        var baseRevision = session.BaseCommitSha is { Length: > 0 } baseSha ? baseSha : repo.BaseBranch;

        FileText? before;
        FileText? after;

        try
        {
            before = await files.ReadAsync(repo, baseRevision, wanted, cancellationToken);
            after = await files.ReadAsync(repo, head, wanted, cancellationToken);
        }
        catch (FileTextUnavailableException exception)
        {
            return new FileDiffRead(FileDiffReadStatus.Unavailable, null, exception.Message);
        }

        return FileDiffRead.Ok(Compose(wanted, before, after));
    }

    /// <summary>Assembles the response from the two sides, saying plainly what it cannot show.</summary>
    public static FileDiffResponse Compose(string path, FileText? before, FileText? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var original = before ?? FileText.Absent;
        var modified = after ?? FileText.Absent;
        var binary = original.Binary || modified.Binary;
        var truncated = original.Truncated || modified.Truncated;

        return new FileDiffResponse
        {
            Path = path,
            Language = MonacoLanguages.For(path),

            // A binary file has no text on either side. Sending one side's bytes as a string would
            // put mojibake in a diff editor and call it a change.
            OriginalText = binary ? string.Empty : original.Text,
            ModifiedText = binary ? string.Empty : modified.Text,

            // Hunks over a truncated file would point at line numbers the reader cannot reach, and
            // hunks over a binary one are meaningless. Both say so instead.
            Hunks = binary || truncated ? [] : TextDiff.Hunks(original.Text, modified.Text),
            Binary = binary,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Every path this change contains, from both sources that know one.
    /// </summary>
    /// <remarks>
    /// The transcript's file writes are one source; the recap's risk-ranked list is the other, and it
    /// may know paths the transcript does not — section 17's provider comparison is a better source
    /// than an agent's own reporting and <c>RecapEvidence</c> says so. Pane 3 lists whichever of the
    /// two the detail carried, so accepting only the transcript's would 404 a file the reviewer can
    /// see in front of them.
    /// </remarks>
    private async Task<HashSet<string>> ChangedPathsAsync(
        Guid sessionId,
        Charter.Domain.Recap? recap,
        CancellationToken cancellationToken)
    {
        var writes = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Type == EventTypes.FileWrite)
            .Select(row => row.Payload)
            .ToListAsync(cancellationToken);

        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var payload in writes)
        {
            if (RequestPresentation.TranscriptPath(EventTypes.FileWrite, payload) is { } path)
            {
                paths.Add(Normalise(path));
            }
        }

        foreach (var item in RecapProjection.RiskItems(recap?.RiskItems))
        {
            paths.Add(Normalise(item.Path));
        }

        return paths;
    }

    private static string Normalise(string? path)
        => path is null ? string.Empty : path.Replace('\\', '/').Trim().TrimStart('/');
}
