using Charter.Data;
using Charter.Domain;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;

namespace Charter.Orchestration;

/// <summary>
/// Promotes events into the four requester-facing milestones of section 11.
/// </summary>
/// <remarks>
/// <para>
/// Pane 1 shows translated milestones, not the raw transcript — but it has to show
/// <em>something</em>. Section 11 is explicit that a five to twenty minute silent gap reads as
/// broken, and a build is exactly that long. Without this, a requester who approved a specification
/// watches an empty thread until a change request appears, which is the longest silent window in the
/// product.
/// </para>
/// <para>
/// The mapping is deliberately small and deliberately coarse. Four labels, one promotion each, in
/// order: a session cannot go back to <em>understanding the current setup</em> once it is
/// <em>making changes</em>, because a thread that walks backwards is worse than one that says
/// nothing. That also makes promotion idempotent without a lock — a redelivered event whose label is
/// already at or behind the session's high-water mark promotes nothing.
/// </para>
/// <para>
/// <strong>Never an ETA.</strong> A milestone carries what happened and when it happened, and the
/// requester's view computes elapsed time from that (section 11). Nothing here predicts a finish.
/// </para>
/// </remarks>
public sealed class SessionMilestones
{
    private readonly CharterDbContext _db;
    private readonly TimeProvider _clock;

    public SessionMilestones(CharterDbContext db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The label an event type is promoted as, or null when it belongs in the engineer view only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>tool_use</c> is <em>understanding the current setup</em> because the reads, greps and
    /// listings an agent opens with are what it is: the tool calls that precede the first write.
    /// <c>file_write</c> is <em>making changes</em>. <c>command</c> and <c>check_result</c> are
    /// <em>checking it works</em> — a session's commands after the writes have started are the build
    /// and the tests. <c>branch_pushed</c> is <em>putting it together</em>, which is the truthful one:
    /// it is the moment the work leaves the sandbox.
    /// </para>
    /// <para>
    /// <c>session_started</c> deliberately promotes as well. It is the only event guaranteed to exist
    /// early, and the first minute of a session is precisely when an empty thread is least tolerable.
    /// </para>
    /// </remarks>
    public static MilestoneLabel? LabelFor(string eventType) => eventType switch
    {
        EventTypes.SessionStarted => MilestoneLabel.UnderstandingSetup,
        EventTypes.ToolUse => MilestoneLabel.UnderstandingSetup,
        EventTypes.FileWrite => MilestoneLabel.MakingChanges,
        EventTypes.Command => MilestoneLabel.CheckingItWorks,
        EventTypes.CheckResult => MilestoneLabel.CheckingItWorks,
        ChangeRequestEventTypes.BranchPushed => MilestoneLabel.PuttingItTogether,
        ChangeRequestEventTypes.ChangeRequestOpened => MilestoneLabel.PuttingItTogether,
        _ => null,
    };

    /// <summary>
    /// Promotes <paramref name="eventId"/> if its type maps to a label the session has not reached.
    /// </summary>
    /// <returns>The label promoted, or null when nothing was.</returns>
    public async Task<MilestoneLabel?> PromoteAsync(
        Guid sessionId,
        Guid eventId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        if (LabelFor(eventType) is not { } label)
        {
            return null;
        }

        // Read back rather than compared in SQL: the label is persisted as text (section 5's enums are
        // stored by name so a reordered enum cannot silently reinterpret existing rows), and the
        // ordering that matters here is the enum's, not the alphabet's.
        var reached = await _db.Milestones
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .Select(row => row.Label)
            .ToListAsync(cancellationToken);

        // Forward only. The labels are declared in the order section 11 lists them, so the enum's own
        // ordering is the progression, and anything at or behind the high-water mark is already said.
        if (reached.Count > 0 && reached.Max() >= label)
        {
            return null;
        }

        _db.Milestones.Add(Milestone.Promote(sessionId, eventId, label, _clock.GetUtcNow()));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two events of the same kind arriving at once. The thread already says it; the loser of
            // the race has nothing to add, and a milestone is not worth failing an event ingest over.
            _db.ChangeTracker.Clear();
            return null;
        }

        return label;
    }

    /// <summary>
    /// Promotes from a session's existing transcript, for a session whose events were recorded before
    /// promotion existed or while the control plane was restarting (section 2.3).
    /// </summary>
    public async Task<IReadOnlyList<MilestoneLabel>> BackfillAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var events = await _db.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.Seq)
            .Select(row => new { row.Id, row.Type })
            .ToListAsync(cancellationToken);

        var promoted = new List<MilestoneLabel>();

        foreach (var row in events)
        {
            if (await PromoteAsync(sessionId, row.Id, row.Type, cancellationToken) is { } label)
            {
                promoted.Add(label);
            }
        }

        return promoted;
    }
}
