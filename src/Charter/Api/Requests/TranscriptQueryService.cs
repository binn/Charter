using System.Globalization;
using Charter.Api.Contracts;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Requests;

/// <summary>Which window of the transcript to fetch. All three are mutually exclusive.</summary>
/// <param name="Cursor">Page backwards from a cursor a previous call returned.</param>
/// <param name="AroundSeq">Centre the window on this sequence (section 12 linkage).</param>
/// <param name="Limit">How many rows to return. Clamped to <see cref="TranscriptProjection.MaxPageSize"/>.</param>
public readonly record struct TranscriptWindow(string? Cursor = null, long? AroundSeq = null, int? Limit = null);

/// <summary>How a transcript read ended.</summary>
public enum TranscriptReadStatus
{
    Ok,

    /// <summary>No such request, or none this member may see anything of (section 7.3).</summary>
    NotFound,

    /// <summary>The request exists and this member may not see pane 2 (section 7.4).</summary>
    Forbidden,
}

/// <summary>A transcript read, with the reason when there is no page.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Page">The window, when there is one.</param>
/// <param name="Reason">Plain language, safe to show (section 11).</param>
public sealed record TranscriptRead(TranscriptReadStatus Status, TranscriptPaneResponse? Page, string Reason)
{
    /// <summary>Builds the success case.</summary>
    public static TranscriptRead Ok(TranscriptPaneResponse page) =>
        new(TranscriptReadStatus.Ok, page, string.Empty);
}

/// <summary>
/// <c>GET /api/requests/{id}/transcript</c> — pane 2, cursor-paginated (section 12).
/// </summary>
/// <remarks>
/// <para>
/// Pane 2 opens at the live tail and pages <em>backwards</em>, which is why the cursor is the lowest
/// sequence already seen rather than an offset: paging cost then does not grow with the transcript,
/// and the unique index on <c>(session_id, seq)</c> serves every query here.
/// </para>
/// <para>
/// <c>aroundSeq</c> is the case that makes the linkage of section 12 usable at all. A milestone can
/// point at event 12 of 12,480, and reaching it by paging backwards twenty-five times is not a user
/// experience — so the window is centred on the sequence instead, in two bounded queries.
/// </para>
/// <para>
/// The permission is section 7.4's, asked of <see cref="SessionVisibilityPolicy"/> through
/// <see cref="RequestVisibility"/>: the same call <c>GET /api/requests/{id}</c> makes when it decides
/// whether to carry a transcript at all. A viewer without repository read gets 403 here and an absent
/// key there, which are the same rule answered in the two shapes the two calls have.
/// </para>
/// </remarks>
public sealed class TranscriptQueryService
{
    private readonly CharterDbContext database;
    private readonly RequestQueryService requests;

    public TranscriptQueryService(CharterDbContext database, RequestQueryService requests)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(requests);

        this.database = database;
        this.requests = requests;
    }

    /// <summary>Reads one window of one request's transcript.</summary>
    public async Task<TranscriptRead> ReadAsync(
        MemberSnapshot member,
        Guid requestId,
        TranscriptWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var view = await requests.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return new TranscriptRead(TranscriptReadStatus.NotFound, null, string.Empty);
        }

        if (!view.Visibility.Transcript)
        {
            // Section 7.4, in the shape a 403 has. The reason is safe to show: it names a permission,
            // not a file path, a branch or anything else pane 2 would have leaked.
            return new TranscriptRead(
                TranscriptReadStatus.Forbidden,
                null,
                "viewing a transcript needs read access to the repository");
        }

        if (view.Aggregate.Session is not { } session)
        {
            // Nothing has run yet. An empty page rather than a 404: the request exists, pane 2 is
            // allowed, and there is simply nothing in it.
            return TranscriptRead.Ok(new TranscriptPaneResponse { Events = [], NextCursor = null, TotalCount = 0 });
        }

        var limit = Clamp(window.Limit);
        var total = await database.Events
            .AsNoTracking()
            .LongCountAsync(row => row.SessionId == session.Id, cancellationToken);

        var events = window switch
        {
            { AroundSeq: { } around } => await AroundAsync(session.Id, around, limit, cancellationToken),
            { Cursor: { } cursor } when TryParseCursor(cursor, out var before)
                => await BeforeAsync(session.Id, before, limit, cancellationToken),
            _ => await TailAsync(session.Id, limit, cancellationToken),
        };

        var hasEarlier = events.Count > 0
                         && await database.Events
                             .AsNoTracking()
                             .AnyAsync(
                                 row => row.SessionId == session.Id && row.Seq < events[0].Seq,
                                 cancellationToken);

        var milestones = await database.Milestones
            .AsNoTracking()
            .Where(row => row.SessionId == session.Id)
            .ToListAsync(cancellationToken);

        var sequences = await SequencesAsync(
            session.Id,
            milestones.Select(row => row.EventId),
            cancellationToken);

        return TranscriptRead.Ok(TranscriptProjection.Page(events, milestones, sequences, total, hasEarlier));
    }

    /// <summary>The live tail — what pane 2 opens on.</summary>
    private async Task<List<Event>> TailAsync(Guid sessionId, int limit, CancellationToken cancellationToken)
    {
        var page = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderByDescending(row => row.Seq)
            .Take(limit)
            .ToListAsync(cancellationToken);

        page.Reverse();
        return page;
    }

    /// <summary>The page before <paramref name="cursor"/>, which is the lowest sequence already held.</summary>
    private async Task<List<Event>> BeforeAsync(
        Guid sessionId,
        long cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Seq < cursor)
            .OrderByDescending(row => row.Seq)
            .Take(limit)
            .ToListAsync(cancellationToken);

        page.Reverse();
        return page;
    }

    /// <summary>
    /// A window centred on one sequence (section 12).
    /// </summary>
    /// <remarks>
    /// Two bounded queries rather than one, because "the half before" and "the half after" are
    /// different orderings and asking Postgres for both at once means an offset. The anchor itself
    /// lands in the first half, so a milestone's own event is always in the page it opens.
    /// </remarks>
    private async Task<List<Event>> AroundAsync(
        Guid sessionId,
        long around,
        int limit,
        CancellationToken cancellationToken)
    {
        var before = Math.Max(1, (limit + 1) / 2);

        var head = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Seq <= around)
            .OrderByDescending(row => row.Seq)
            .Take(before)
            .ToListAsync(cancellationToken);

        head.Reverse();

        var tail = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Seq > around)
            .OrderBy(row => row.Seq)
            .Take(limit - head.Count)
            .ToListAsync(cancellationToken);

        head.AddRange(tail);
        return head;
    }

    private async Task<IReadOnlyDictionary<Guid, long>> SequencesAsync(
        Guid sessionId,
        IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        var wanted = eventIds.Distinct().ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, long>();
        }

        return await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && wanted.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.Seq, cancellationToken);
    }

    private static int Clamp(int? limit) => limit switch
    {
        null or < 1 => TranscriptProjection.DefaultPageSize,
        > TranscriptProjection.MaxPageSize => TranscriptProjection.MaxPageSize,
        _ => limit.Value,
    };

    /// <summary>
    /// The cursor is opaque to the client, which never parses one. A cursor that is not a sequence is
    /// therefore not a client error worth a 400 — it is a stale link, and the tail is the honest
    /// answer to one.
    /// </summary>
    private static bool TryParseCursor(string cursor, out long seq)
        => long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq) && seq > 0;
}
