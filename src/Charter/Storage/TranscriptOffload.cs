using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Charter.Storage;

/// <summary>
/// Moves the oversized strings out of a transcript event and leaves a reference behind
/// (sections 5, 19, 27.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is the consumer.</strong> <c>events</c> is the largest table in the schema by
/// orders of magnitude (section 5), and the thing that makes a row large is never the row: it is one
/// string inside it. An adapter's <c>raw</c> payload carries the agent's entire JSONL line, and a
/// <c>Write</c> tool call contains the whole file it wrote; a <c>command</c> event carries what the
/// command said; a <c>check_result</c> carries the tail of a build log. Those are real bytes,
/// produced on every session, and putting a half-megabyte file body into a jsonb column is exactly
/// the problem an object store exists to solve. It is also a producer that exists <em>today</em>,
/// which the section 27.1 artifact kinds do not - <c>build_artifact</c>, <c>capture</c> and
/// <c>hil_report</c> arrive with the project types that emit them (section 23), and wiring a store to
/// a writer that will not exist for two phases would rebuild the defect this replaced.
/// </para>
/// <para>
/// <strong>What the event still says.</strong> The oversized string is replaced by its tail, not
/// removed - a build log's reason is at the end, and pane 2 must stay readable without a second
/// fetch. Beside it go <c>{property}_ref</c>, the object key, and <c>{property}_bytes</c>, the
/// original size. Anything reading the payload for structure - the milestone promoter, the change
/// request body, section 6's question handling - reads scalars and is untouched.
/// </para>
/// <para>
/// <strong>It never fails a session.</strong> Storage being unreachable is an operator problem;
/// losing a transcript event over it would be Charter's. Every failure path returns the original
/// payload and logs, so the worst case is the row it would have shrunk.
/// </para>
/// <para>
/// <strong>The label is untrusted (section 16).</strong> The readable part of the key comes from the
/// payload - a check name from the repository's <c>.charter/config.yml</c>, a command line, a file
/// path from a tool call - all of which are written by whoever opened the pull request or by a model.
/// A check named <c>../../etc/passwd</c> is a realistic input, and <see cref="ObjectKey.Segment"/> is
/// what makes it one harmless path component.
/// </para>
/// </remarks>
public sealed class TranscriptOffload
{
    /// <summary>The longest string kept inline in a payload, 8 KiB.</summary>
    /// <remarks>
    /// Above any legitimate message and below anything that makes a row expensive. Chosen so that the
    /// common transcript event - a tool call, a status line, a check summary - is untouched, and only
    /// the outliers move.
    /// </remarks>
    public const int MaxInlineChars = 8 * 1024;

    /// <summary>How much of the original is kept inline once it has been offloaded.</summary>
    public const int InlineTailChars = 2 * 1024;

    /// <summary>What is prepended to the kept tail, so a reader can see it is one.</summary>
    public const string TruncationMarker = "…";

    /// <summary>How deep into a payload the walk goes before it stops.</summary>
    private const int MaxDepth = 12;

    /// <summary>The properties whose value names the object, in preference order.</summary>
    private static readonly string[] LabelProperties = ["check", "command", "path", "reason", "ecosystem"];

    private readonly IObjectStore _store;
    private readonly ILogger<TranscriptOffload> _logger;

    /// <summary>Creates the offload against a store.</summary>
    public TranscriptOffload(IObjectStore store, ILogger<TranscriptOffload> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <summary>True when this instance has somewhere to put bytes.</summary>
    public bool Enabled => _store.Enabled;

    /// <summary>
    /// Rewrites one event's payload, storing anything oversized.
    /// </summary>
    /// <param name="sessionId">The session the event belongs to. Decides the key prefix.</param>
    /// <param name="type">The event type, used to name the object when the payload has no label.</param>
    /// <param name="payloadJson">The payload as it arrived.</param>
    /// <param name="eventKey">
    /// The event's idempotency key. Hashed into the object key so a redelivered event overwrites its
    /// own object instead of accumulating a second copy of it.
    /// </param>
    /// <returns>
    /// The payload to store. The original string, unchanged, whenever storage is off, nothing is
    /// oversized, the payload is not a JSON object, or the store refused.
    /// </returns>
    public async Task<string> RewriteAsync(
        Guid sessionId,
        string type,
        string payloadJson,
        string eventKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (!_store.Enabled || payloadJson.Length <= MaxInlineChars)
        {
            // The length test is over the serialised payload, so a payload that could not contain an
            // oversized string is never parsed at all. This is the hot path: almost every event.
            return payloadJson;
        }

        JsonObject? payload;

        try
        {
            payload = JsonNode.Parse(payloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            return payloadJson;
        }

        if (payload is null)
        {
            return payloadJson;
        }

        var label = Label(payload, type);
        var token = ObjectKey.Token(eventKey);
        var stored = 0;

        try
        {
            stored = await OffloadAsync(payload, sessionId, label, token, string.Empty, 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectStoreException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception,
                "Could not offload transcript output for session {SessionId}; the event is stored whole instead",
                sessionId);

            return payloadJson;
        }

        return stored == 0 ? payloadJson : payload.ToJsonString();
    }

    /// <summary>
    /// Replaces every oversized string reachable from <paramref name="node"/>. Returns how many moved.
    /// </summary>
    private async Task<int> OffloadAsync(
        JsonNode node,
        Guid sessionId,
        string label,
        string token,
        string path,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            return 0;
        }

        var moved = 0;

        switch (node)
        {
            case JsonObject payload:
                foreach (var (name, value) in payload.ToArray())
                {
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && text.Length > MaxInlineChars)
                    {
                        var reference = await StoreAsync(
                            sessionId,
                            label,
                            token,
                            Join(path, name),
                            text,
                            cancellationToken).ConfigureAwait(false);

                        if (reference is null)
                        {
                            continue;
                        }

                        payload[name] = Tail(text);
                        payload[name + "_ref"] = reference.Key;
                        payload[name + "_bytes"] = reference.SizeBytes;
                        payload[name + "_sha256"] = reference.Sha256;
                        moved++;
                        continue;
                    }

                    if (value is JsonObject or JsonArray)
                    {
                        moved += await OffloadAsync(
                            value,
                            sessionId,
                            label,
                            token,
                            Join(path, name),
                            depth + 1,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is { } item and (JsonObject or JsonArray))
                    {
                        moved += await OffloadAsync(
                            item,
                            sessionId,
                            label,
                            token,
                            Join(path, index.ToString(CultureInfo.InvariantCulture)),
                            depth + 1,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                break;
        }

        return moved;
    }

    private async Task<StoredObject?> StoreAsync(
        Guid sessionId,
        string label,
        string token,
        string property,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        if (bytes.Length > _store.MaxObjectBytes)
        {
            // Bounded before the write, never at it. An object store that only grows is an operator's
            // problem handed to them by somebody else, so the cap is enforced here and the fact that
            // it bit is recorded in the key rather than left to be inferred from a size.
            bytes = Truncate(bytes, (int)_store.MaxObjectBytes);
            property += ".truncated";
        }

        var key = ObjectKey.WithExtension(
            ObjectKey.ForSession(sessionId, "transcript", label, $"{token}-{property}"),
            "txt");

        return await _store
            .PutAsync(key, bytes, ObjectContentTypes.Text, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The readable part of the key. Every source of it is untrusted (section 16).</summary>
    private static string Label(JsonObject payload, string type)
    {
        foreach (var property in LabelProperties)
        {
            if (payload[property] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return ObjectKey.Segment(text);
            }
        }

        return ObjectKey.Segment(type);
    }

    /// <summary>The end of the string, which is where a build tool puts the reason.</summary>
    private static string Tail(string text)
        => TruncationMarker + text[^InlineTailChars..];

    /// <summary>Cuts UTF-8 bytes to a limit without splitting a character in half.</summary>
    private static byte[] Truncate(byte[] bytes, int limit)
    {
        var length = limit;

        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return bytes[..length];
    }

    private static string Join(string path, string name)
        => path.Length == 0 ? name : path + "." + name;
}
