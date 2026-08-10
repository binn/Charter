using System.Text;
using System.Text.Json;
using Charter.Domain;
using Charter.Refinement;

namespace Charter.Teaching;

/// <summary>
/// The completed session's real events, which is the whole reason teaching is worth paying for.
/// </summary>
/// <remarks>
/// Section 13 is blunt about the bar: <em>generic content is worthless</em>. Its own example —
/// <em>"your quote wizard stores the selected vertical in a table called Quotes, and adding this
/// meant one new column"</em> — names the reader's own feature, the reader's own table and the exact
/// size of the change. Nothing in that sentence could have been written without the transcript, and
/// that is the point: an explanation that could have been written before the session ran is an
/// article, and nobody needs Charter to fetch them an article.
/// </remarks>
public sealed record TeachingEvidence
{
    /// <summary>The session being explained.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The approved specification, in its requester rendering.</summary>
    public required SpecDocument Spec { get; init; }

    /// <summary>Pane-1 milestones, in order (section 11).</summary>
    public IReadOnlyList<Milestone> Milestones { get; init; } = [];

    /// <summary>The session's transcript, oldest first.</summary>
    public IReadOnlyList<Event> Events { get; init; } = [];

    /// <summary>The files the session touched, for grounding.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>What this project is called, in the reader's own words.</summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// This organisation's own vocabulary (section 8, <c>glossary.yml</c>). Teaching uses the words
    /// the reader already uses rather than inventing synonyms for them.
    /// </summary>
    public string? GlossaryText { get; init; }

    /// <summary>
    /// Nothing actually happened in this session, so there is nothing grounded to say about it.
    /// </summary>
    public bool IsEmpty => Events.Count == 0 && Milestones.Count == 0 && ChangedFiles.Count == 0;
}

/// <summary>Renders a session's real events for a teaching prompt.</summary>
public static class TeachingEvidenceRenderer
{
    private static readonly string[] InterestingKeys =
    [
        "path", "file_path", "file", "filename", "command", "text", "message", "summary", "status",
    ];

    /// <summary>The requester-facing words for a milestone (section 11).</summary>
    public static string Describe(MilestoneLabel label) => label switch
    {
        MilestoneLabel.UnderstandingSetup => "understanding the current setup",
        MilestoneLabel.MakingChanges => "making changes",
        MilestoneLabel.CheckingItWorks => "checking it works",
        MilestoneLabel.PuttingItTogether => "putting it together",
        _ => "working",
    };

    /// <summary>Renders the milestone list, numbered so the model can annotate by index.</summary>
    public static string RenderMilestones(IReadOnlyList<Milestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        if (milestones.Count == 0)
        {
            return "(no milestones were promoted for this session)";
        }

        var builder = new StringBuilder();
        for (var index = 0; index < milestones.Count; index++)
        {
            builder
                .Append(index).Append(". ").Append(Describe(milestones[index].Label))
                .Append(" (event ").Append(milestones[index].EventId).AppendLine(")");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders the transcript, capped and excerpted.</summary>
    public static string RenderEvents(
        IReadOnlyList<Event> events,
        int maxEvents,
        int maxPayloadCharacters)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEvents);

        if (events.Count == 0)
        {
            return "(no events were recorded for this session)";
        }

        var ordered = events
            .Where(static @event => @event is not null)
            .OrderBy(static @event => @event.Seq)
            .ToList();

        var builder = new StringBuilder();
        var omitted = Math.Max(0, ordered.Count - maxEvents);
        foreach (var @event in ordered.Take(maxEvents))
        {
            builder
                .Append('#').Append(@event.Seq).Append(' ').Append(@event.Type).Append(": ")
                .AppendLine(Excerpt(@event.Payload, maxPayloadCharacters));
        }

        if (omitted > 0)
        {
            builder.Append('(').Append(omitted).AppendLine(" later events omitted.)");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders the files the session touched.</summary>
    public static string RenderFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return "(no file changes were recorded)";
        }

        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append("- ").AppendLine(file);
        }

        return builder.ToString().TrimEnd();
    }

    private static string Excerpt(string payload, int maxCharacters)
    {
        var text = Summarise(payload).ReplaceLineEndings(" ").Trim();
        return text.Length <= maxCharacters ? text : text[..maxCharacters] + "…";
    }

    private static string Summarise(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return payload;
            }

            var builder = new StringBuilder();
            foreach (var key in InterestingKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    builder.Append(key).Append('=').Append(value.GetString()).Append("; ");
                }
            }

            return builder.Length > 0 ? builder.ToString() : payload;
        }
        catch (JsonException)
        {
            return payload;
        }
    }
}
