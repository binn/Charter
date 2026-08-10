using System.Text.Json;
using System.Text.Json.Nodes;
using Charter.Adapters;
using Charter.Domain;

namespace Charter.Runners.Shim;

/// <summary>One event the shim posts to <c>{callback_url}/events</c>.</summary>
/// <param name="Type">A Charter event type (see <see cref="EventTypes"/>). An open string, by design.</param>
/// <param name="Payload">jsonb, already serialised.</param>
/// <param name="Index">
/// The shim's own monotonic counter for this run. The control plane assigns the durable
/// <see cref="Event.Seq"/>; this is what makes a replay after a reconnect idempotent rather than a
/// second copy of the transcript (section 2.3).
/// </param>
public sealed record ShimOutboundEvent(string Type, string Payload, long Index);

/// <summary>What one line of agent output turned into.</summary>
/// <param name="Kind">How the adapter classified it.</param>
/// <param name="Event">The event to publish, if any.</param>
/// <param name="Violation">
/// Set when the line reported a write the path scope refuses. The session stops; see
/// <see cref="ShimSession"/>.
/// </param>
/// <param name="Paths">
/// The workspace-relative files the line reported writing, already scope-checked. The shim inspects
/// these for generated migrations (section 15) — the one thing that has to look at a write's
/// <em>content</em>, not just its path.
/// </param>
public sealed record ShimLineOutcome(
    AdapterLineKind Kind,
    ShimOutboundEvent? Event,
    PathScopeDecision? Violation,
    IReadOnlyList<string>? Paths = null);

/// <summary>
/// Turns an agent's output stream into Charter events, enforcing path scope as it goes.
/// </summary>
/// <remarks>
/// Event mapping itself is the adapter's job and is not reimplemented here:
/// <see cref="AdapterEventClassifier"/> already owns it, was validated at load time, and is total by
/// construction. What this adds is the two things only the shim can do — attach the file paths a
/// write touched, and refuse the ones outside the scope (section 7.3).
/// </remarks>
public sealed class ShimEventTranslator
{
    private readonly AdapterEventClassifier _classifier;
    private readonly ShimPathScope _scope;
    private long _index;

    public ShimEventTranslator(AdapterEventClassifier classifier, ShimPathScope scope)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(scope);

        _classifier = classifier;
        _scope = scope;
    }

    /// <summary>How many lines the adapter declared jsonl for but could not parse.</summary>
    public int MalformedLines { get; private set; }

    /// <summary>Translates one line. Never throws: one bad line must not end a session.</summary>
    public ShimLineOutcome Translate(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var classification = _classifier.Classify(line);

        switch (classification.Kind)
        {
            case AdapterLineKind.Blank:
            case AdapterLineKind.Unmatched:
                return new ShimLineOutcome(classification.Kind, null, null);

            case AdapterLineKind.Malformed:
                MalformedLines++;
                return new ShimLineOutcome(classification.Kind, null, null);

            case AdapterLineKind.Raw:
                // events.format: text. Pane 2 degrades to a raw log, which docs/adapters.md documents
                // rather than pretending parity (section 12b).
                return new ShimLineOutcome(
                    classification.Kind,
                    Build(EventTypes.Message, new JsonObject { ["text"] = line, ["raw"] = true }),
                    null);

            default:
                return Matched(line, classification);
        }
    }

    /// <summary>An event the shim raises itself, rather than one the agent produced.</summary>
    public ShimOutboundEvent Synthesize(string type, JsonObject payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);

        return Build(type, payload);
    }

    private ShimLineOutcome Matched(string line, AdapterLineClassification classification)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            MalformedLines++;
            return new ShimLineOutcome(AdapterLineKind.Malformed, null, null);
        }

        var type = ToEventType(classification.Primary!.Value);
        var paths = classification.Matches.Contains(AdapterEventType.FileWrite) && parsed is not null
            ? ShimFilePaths.Extract(parsed)
            : [];

        foreach (var path in paths)
        {
            var decision = _scope.Evaluate(path);
            if (!decision.Allowed)
            {
                return new ShimLineOutcome(classification.Kind, null, decision);
            }
        }

        var payload = new JsonObject
        {
            ["adapter"] = _classifier.Adapter.Id,
            ["raw"] = parsed,
        };

        string[]? resolved = null;

        if (paths.Count > 0)
        {
            resolved = [.. paths.Select(path => _scope.Evaluate(path).Path)];

            var array = new JsonArray();
            foreach (var path in resolved)
            {
                array.Add(path);
            }

            payload["paths"] = array;
        }

        return new ShimLineOutcome(classification.Kind, Build(type, payload), null, resolved);
    }

    private ShimOutboundEvent Build(string type, JsonObject payload)
        => new(type, payload.ToJsonString(), ++_index);

    private static string ToEventType(AdapterEventType type) => type switch
    {
        AdapterEventType.FileWrite => EventTypes.FileWrite,
        AdapterEventType.ToolUse => EventTypes.ToolUse,
        _ => EventTypes.Message,
    };
}

/// <summary>
/// Pulls the file paths out of an agent's tool-call JSON.
/// </summary>
/// <remarks>
/// The adapter schema (section 12b) declares how to <em>classify</em> a line, not where the path sits
/// inside it, and the CLIs disagree: Claude Code puts it at <c>message.content[0].input.file_path</c>,
/// others at <c>path</c> or <c>file</c>. Rather than adding a path expression to every adapter — a
/// schema change with seven files to keep in step — the shim looks for the handful of property names
/// the ecosystem actually uses, anywhere in the object.
/// <para>
/// Being generous here is the safe direction: an extra candidate path gets scope-checked and, if it
/// is inside the scope, changes nothing. A path this misses is a write that was not scope-checked,
/// which is why the search is by name across the whole document rather than at a fixed location.
/// </para>
/// </remarks>
public static class ShimFilePaths
{
    private const int MaxDepth = 12;

    private static readonly HashSet<string> PathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "file",
        "filepath",
        "file_path",
        "filename",
        "file_name",
        "notebook_path",
        "notebookpath",
        "target_file",
        "targetfile",
        "old_path",
        "new_path",
    };

    /// <summary>Every distinct path-shaped string in the document, in document order.</summary>
    public static IReadOnlyList<string> Extract(JsonNode? node)
    {
        var found = new List<string>();
        Walk(node, 0, found);
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    private static void Walk(JsonNode? node, int depth, List<string> found)
    {
        if (node is null || depth > MaxDepth)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (PathProperties.Contains(name)
                        && value is JsonValue leaf
                        && leaf.TryGetValue<string>(out var path)
                        && !string.IsNullOrWhiteSpace(path))
                    {
                        found.Add(path);
                    }
                    else
                    {
                        Walk(value, depth + 1, found);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, depth + 1, found);
                }

                break;
        }
    }
}
