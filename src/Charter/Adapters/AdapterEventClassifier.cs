using System.Text.Json;

namespace Charter.Adapters;

/// <summary>What one line of an agent's output turned out to be.</summary>
/// <param name="Matches">
/// Every event type the line matched, most specific first: a single JSON object can legitimately be
/// both a <c>tool_use</c> and a <c>file_write</c>, and the shipped adapters rely on that.
/// </param>
public sealed record AdapterLineClassification(
    AdapterLineKind Kind,
    IReadOnlyList<AdapterEventType> Matches)
{
    public static AdapterLineClassification Blank { get; } = new(AdapterLineKind.Blank, []);

    public static AdapterLineClassification Raw { get; } = new(AdapterLineKind.Raw, []);

    public static AdapterLineClassification Malformed { get; } = new(AdapterLineKind.Malformed, []);

    /// <summary>The event type to record for this line, or <see langword="null"/> if none matched.</summary>
    public AdapterEventType? Primary => Matches.Count == 0 ? null : Matches[0];
}

/// <summary>The five things a line can be.</summary>
public enum AdapterLineKind
{
    /// <summary>Whitespace only. Not an event and not a problem.</summary>
    Blank,

    /// <summary>The adapter declares <c>events.format: text</c>, so the line is log output.</summary>
    Raw,

    /// <summary>Valid JSON that matched no mapping. Normal: agents emit lines Charter does not model.</summary>
    Unmatched,

    /// <summary>Valid JSON that matched at least one mapping.</summary>
    Matched,

    /// <summary>
    /// A <c>jsonl</c> adapter emitted a line that is not JSON. Kept as its own kind rather than
    /// folded into <see cref="Unmatched"/>: a stream that is mostly malformed means the adapter's
    /// invocation is wrong, and that is worth surfacing rather than dropping on the floor.
    /// </summary>
    Malformed,
}

/// <summary>
/// Turns an agent's raw output lines into Charter event types using an adapter's <c>events.map</c>.
/// </summary>
/// <remarks>
/// Total by construction: the predicates were parsed and validated at load time, and every failure
/// mode of a line — blank, truncated, hostile, or simply unmodelled — has a classification rather
/// than an exception. This runs inside the shim on every line of a live session, so a throw here
/// would take down a session over one bad line.
/// </remarks>
public sealed class AdapterEventClassifier
{
    private static readonly AdapterEventType[] Specificity =
    [
        AdapterEventType.FileWrite,
        AdapterEventType.ToolUse,
        AdapterEventType.Message,
    ];

    private readonly AdapterDocument _adapter;
    private readonly AdapterEventMapping[] _mappings;

    public AdapterEventClassifier(AdapterDocument adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        _adapter = adapter;
        _mappings = [.. adapter.Events.Map.OrderBy(mapping => Array.IndexOf(Specificity, mapping.EventType))];
    }

    public AdapterDocument Adapter => _adapter;

    public AdapterLineClassification Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(line))
        {
            return AdapterLineClassification.Blank;
        }

        if (_adapter.Events.Format != AdapterEventFormat.Jsonl)
        {
            return AdapterLineClassification.Raw;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return AdapterLineClassification.Malformed;
        }

        using (parsed)
        {
            var matches = new List<AdapterEventType>();
            foreach (var mapping in _mappings)
            {
                if (!matches.Contains(mapping.EventType) && mapping.Predicate.Evaluate(parsed.RootElement))
                {
                    matches.Add(mapping.EventType);
                }
            }

            return matches.Count == 0
                ? new AdapterLineClassification(AdapterLineKind.Unmatched, [])
                : new AdapterLineClassification(AdapterLineKind.Matched, matches);
        }
    }
}
