using System.Text;
using System.Text.Json;
using Charter.Domain;
using Charter.Refinement;

namespace Charter.Recaps;

/// <summary>
/// Everything the engineer recap runs over: the approved spec, the session's real events, and the
/// files the session actually touched.
/// </summary>
/// <remarks>
/// Section 14 is the walkthrough's twin — <em>same event stream, different prompt</em> — and the
/// event stream is the point. A recap assembled from the spec alone would describe what was asked
/// for rather than what happened, which is precisely the gap the deviations section exists to close.
/// </remarks>
public sealed record RecapEvidence
{
    /// <summary>The session being recapped.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The approved specification. Section 14's first section ties back to this.</summary>
    public required SpecDocument Spec { get; init; }

    /// <summary>
    /// Section 7.5: nobody vetted the spec before the build. The recap leads with that and includes
    /// the spec in full rather than summarised.
    /// </summary>
    public bool AutoDispatched { get; init; }

    /// <summary>Who approved the spec, where somebody did. Never shown when auto-dispatched.</summary>
    public string? ApprovedBy { get; init; }

    /// <summary>The files the session touched.</summary>
    public IReadOnlyList<RecapFileChange> Files { get; init; } = [];

    /// <summary>The session's transcript, oldest first.</summary>
    public IReadOnlyList<Event> Events { get; init; } = [];

    /// <summary>
    /// The repository's <c>scopes.deny</c> globs (section 8), so denylist-adjacent paths can float.
    /// </summary>
    public IReadOnlyList<string> DenyPatterns { get; init; } = [];

    /// <summary>The provider's own word for a change request, so the body reads correctly (part A.2).</summary>
    public string ChangeRequestTerm { get; init; } = "change request";

    /// <summary>The agent that produced the work, for the header line.</summary>
    public string? AgentModel { get; init; }
}

/// <summary>
/// Reads the session's transcript for the two things the recap needs from it: which files were
/// written, and what the agent did and said along the way.
/// </summary>
public static class RecapEventReader
{
    private static readonly string[] PathKeys =
    [
        "path", "file_path", "filePath", "file", "filename", "fileName", "target", "target_file",
        "relative_path", "relativePath",
    ];

    private static readonly string[] TextKeys = ["text", "message", "content", "summary", "command", "output"];

    /// <summary>
    /// Best-effort extraction of the files a session wrote, from its <c>file_write</c> events.
    /// </summary>
    /// <remarks>
    /// Adapters are data, not code (section 12b), so an event payload is whatever the agent's own
    /// CLI emitted and there is no single field name to read. This looks for the shapes the shipped
    /// adapters produce and skips anything it cannot understand rather than guessing. A caller that
    /// has the provider's own comparison (section 17) should prefer it and pass those paths instead.
    /// </remarks>
    public static IReadOnlyList<RecapFileChange> ReadFileChanges(IEnumerable<Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var seen = new Dictionary<string, RecapFileChange>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var @event in events)
        {
            if (@event is null || !string.Equals(@event.Type, EventTypes.FileWrite, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var path in ExtractPaths(@event.Payload))
            {
                var normalised = GlobPattern.Normalise(path);
                if (normalised.Length == 0 || seen.ContainsKey(normalised))
                {
                    continue;
                }

                seen[normalised] = new RecapFileChange(normalised);
                order.Add(normalised);
            }
        }

        return [.. order.Select(path => seen[path])];
    }

    /// <summary>
    /// Renders the transcript for the prompt: type, sequence, and a truncated payload excerpt.
    /// </summary>
    /// <remarks>
    /// The excerpt is fenced and labelled as transcript data. Section 16: repository content and tool
    /// output are untrusted, and a recap prompt is one of the places that reads them.
    /// </remarks>
    public static string Render(IEnumerable<Event> events, int maxEvents, int maxPayloadCharacters)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEvents);

        var all = events.Where(static @event => @event is not null).OrderBy(static @event => @event.Seq).ToList();
        var selected = Select(all, maxEvents);

        var builder = new StringBuilder();
        if (all.Count > selected.Count)
        {
            builder
                .Append("(")
                .Append(all.Count - selected.Count)
                .AppendLine(" further events omitted; the ones below are the ones that changed something.)")
                .AppendLine();
        }

        foreach (var @event in selected)
        {
            builder
                .Append('#').Append(@event.Seq).Append(' ').Append(@event.Type).Append(": ")
                .AppendLine(Excerpt(@event.Payload, maxPayloadCharacters));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Whichever events matter most, capped. Errors, checks, writes and commands are kept first;
    /// chatter is dropped before anything that changed state.
    /// </summary>
    private static List<Event> Select(List<Event> all, int maxEvents)
    {
        if (all.Count <= maxEvents)
        {
            return all;
        }

        var ranked = all
            .Select((@event, index) => (Event: @event, Index: index, Weight: Weight(@event.Type)))
            .OrderByDescending(static entry => entry.Weight)
            .ThenBy(static entry => entry.Index)
            .Take(maxEvents)
            .OrderBy(static entry => entry.Index)
            .Select(static entry => entry.Event);

        return [.. ranked];
    }

    private static int Weight(string type) => type switch
    {
        EventTypes.Error => 5,
        EventTypes.CheckResult => 4,
        EventTypes.FileWrite => 3,
        EventTypes.Command => 2,
        EventTypes.NetworkCall => 2,
        EventTypes.Message => 1,
        _ => 0,
    };

    private static string Excerpt(string payload, int maxCharacters)
    {
        var text = Summarise(payload);
        text = text.ReplaceLineEndings(" ").Trim();
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
            foreach (var key in PathKeys.Concat(TextKeys))
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

    private static IEnumerable<string> ExtractPaths(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var path in Walk(document.RootElement, depth: 0))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> Walk(JsonElement element, int depth)
    {
        if (depth > 6)
        {
            yield break;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && PathKeys.Contains(property.Name, StringComparer.Ordinal))
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            yield return value;
                        }

                        continue;
                    }

                    foreach (var nested in Walk(property.Value, depth + 1))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Walk(item, depth + 1))
                    {
                        yield return nested;
                    }
                }

                break;

            default:
                break;
        }
    }
}
