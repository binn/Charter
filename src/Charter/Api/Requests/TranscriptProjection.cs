using System.Globalization;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Turns stored <see cref="Event"/> rows into pane 2 (section 12).
/// </summary>
/// <remarks>
/// <para>
/// Section 12b makes adapters data rather than code, so the adapter's own event name is carried
/// verbatim as <see cref="TranscriptEventResponse.Type"/> and the client never switches on it. What
/// the client does switch on is <see cref="TranscriptEventResponse.Kind"/>, and this file is where
/// that projection is made — once, over Charter's own event vocabulary, which is what an adapter's
/// <c>events.map</c> block already resolved to before the row was written.
/// </para>
/// <para>
/// That indirection is the whole point. A new adapter calling a file write
/// <c>tool_execution_start</c> is a configuration PR, and pane 2 keeps drawing the file-write icon
/// without a Charter release, because the mapping it depends on happens at ingest rather than here.
/// </para>
/// </remarks>
public static class TranscriptProjection
{
    /// <summary>How many transcript rows one page of pane 2 carries by default (section 12).</summary>
    public const int DefaultPageSize = 200;

    /// <summary>The most rows one request may ask for. A window, not a download.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// The adapter-independent classification of one stored event.
    /// </summary>
    /// <remarks>
    /// Unknown types are <see cref="ApiTranscriptEventKind.Message"/> rather than a throw:
    /// <see cref="Event.Type"/> is an open string by design (section 12b), and an adapter emitting a
    /// type this build has never heard of must still render as a row rather than take the pane down.
    /// </remarks>
    public static ApiTranscriptEventKind KindOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type switch
        {
            EventTypes.FileWrite => ApiTranscriptEventKind.FileWrite,
            EventTypes.ToolUse => ApiTranscriptEventKind.ToolUse,
            EventTypes.Command => ApiTranscriptEventKind.Command,
            EventTypes.Message => ApiTranscriptEventKind.Message,
            EventTypes.CheckResult or EventTypes.Error or EventTypes.NetworkCall
                => ApiTranscriptEventKind.Diagnostic,
            EventTypes.SessionStarted or EventTypes.SessionEnded or EventTypes.Cost
                => ApiTranscriptEventKind.Lifecycle,

            // The orchestrator's own journal entries (dispatched, queued, resumed, cancel
            // requested). They are facts about the run rather than about the code, which is exactly
            // what `lifecycle` names.
            _ when type.StartsWith("session_", StringComparison.Ordinal) => ApiTranscriptEventKind.Lifecycle,
            _ => ApiTranscriptEventKind.Message,
        };
    }

    /// <summary>
    /// How loud a row is. Absent — not <c>info</c> — for the ordinary case.
    /// </summary>
    /// <remarks>
    /// Section 27.7's rule generalises: never colour alone, so the client pairs this with an icon and
    /// a word. Only events that genuinely carry a severity get one; stamping <c>info</c> on every row
    /// would make the field meaningless and the icon column noise.
    /// </remarks>
    public static ApiTranscriptLevel? LevelOf(string type, string? payloadJson)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (string.Equals(type, EventTypes.Error, StringComparison.Ordinal)
            || string.Equals(type, "session_dispatch_failed", StringComparison.Ordinal))
        {
            return ApiTranscriptLevel.Error;
        }

        if (string.Equals(type, EventTypes.CheckResult, StringComparison.Ordinal))
        {
            // A check that reported a failure is an error; one that passed is not a warning.
            return Failed(payloadJson) ? ApiTranscriptLevel.Error : null;
        }

        return null;
    }

    /// <summary>One page of pane 2, already ordered oldest-first.</summary>
    /// <param name="events">The window, in any order.</param>
    /// <param name="milestones">The session's milestones, for the run marking section 12 asks for.</param>
    /// <param name="sequences">Event id to sequence, so a milestone's anchor can be found by seq.</param>
    /// <param name="totalCount">Every event in the session, not just this page.</param>
    /// <param name="hasEarlier">Whether anything older than this page exists.</param>
    public static TranscriptPaneResponse Page(
        IReadOnlyList<Event> events,
        IReadOnlyList<Milestone> milestones,
        IReadOnlyDictionary<Guid, long> sequences,
        long totalCount,
        bool hasEarlier)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(milestones);
        ArgumentNullException.ThrowIfNull(sequences);

        var anchors = Anchors(milestones, sequences);
        var ordered = events.OrderBy(row => row.Seq).ToList();
        var rows = new List<TranscriptEventResponse>(ordered.Count);

        foreach (var row in ordered)
        {
            rows.Add(new TranscriptEventResponse
            {
                Seq = row.Seq,
                Kind = KindOf(row.Type),
                Type = row.Type,
                Summary = RequestPresentation.TranscriptSummary(row.Type, row.Payload),
                CreatedAt = row.CreatedAt,
                Path = RequestPresentation.TranscriptPath(row.Type, row.Payload),

                // Absent unless the write itself reported one. An index invented here would send
                // pane 3 to a hunk the agent never touched, which is worse than not jumping.
                HunkIndex = HunkIndexOf(row.Type, row.Payload),
                MilestoneId = MilestoneFor(anchors, row.Seq),
                Level = LevelOf(row.Type, row.Payload),
            });
        }

        return new TranscriptPaneResponse
        {
            Events = rows,

            // Section 12: the cursor is the lowest sequence already fetched, never an offset, so
            // paging cost does not grow with the transcript.
            NextCursor = hasEarlier && rows.Count > 0
                ? rows[0].Seq.ToString(CultureInfo.InvariantCulture)
                : null,
            TotalCount = totalCount,
        };
    }

    /// <summary>
    /// Each milestone's anchor sequence, ascending.
    /// </summary>
    /// <remarks>
    /// A milestone is promoted from one event, and section 12 wants pane 2 to mark <em>the run</em>
    /// of events it produced rather than only that first line. The run is therefore everything from
    /// one anchor up to the next, which needs no extra column and cannot disagree with the milestone
    /// list pane 1 is already showing.
    /// </remarks>
    private static IReadOnlyList<(long Seq, string Id)> Anchors(
        IReadOnlyList<Milestone> milestones,
        IReadOnlyDictionary<Guid, long> sequences)
    {
        var anchors = new List<(long Seq, string Id)>(milestones.Count);

        foreach (var milestone in milestones)
        {
            if (sequences.TryGetValue(milestone.EventId, out var seq))
            {
                anchors.Add((seq, milestone.Id.ToString()));
            }
        }

        anchors.Sort((left, right) => left.Seq.CompareTo(right.Seq));
        return anchors;
    }

    private static string? MilestoneFor(IReadOnlyList<(long Seq, string Id)> anchors, long seq)
    {
        string? found = null;

        foreach (var (anchor, id) in anchors)
        {
            if (anchor > seq)
            {
                break;
            }

            found = id;
        }

        return found;
    }

    /// <summary>The hunk a file write named, when it named one.</summary>
    private static int? HunkIndexOf(string type, string? payloadJson)
    {
        if (!string.Equals(type, EventTypes.FileWrite, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "hunk_index", "hunkIndex", "hunk" })
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var index)
                    && index >= 0)
                {
                    return index;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool Failed(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("passed", out var passed)
                && passed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return passed.ValueKind == JsonValueKind.False;
            }

            return document.RootElement.TryGetProperty("failed", out var failed)
                   && ((failed.ValueKind == JsonValueKind.True)
                       || (failed.ValueKind == JsonValueKind.Number
                           && failed.TryGetInt32(out var count)
                           && count > 0));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
