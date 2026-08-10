using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Charter.Orchestration;

/// <summary>
/// The other shape a <see cref="Domain.JobType.Build"/> job arrives in: an approved specification,
/// with no session yet.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BuildJobPayload"/> names a session, which is what the dispatcher needs. The API writes
/// this one instead, because at the moment the spend gate opens (section 7.5) there is a request and
/// a specification and nothing else. Something has to turn the second into the first, and this is the
/// payload that says so.
/// </para>
/// <para>
/// Both spellings of every field are accepted. The API serialises through
/// <c>CharterApiJson.Options</c>, which is camelCase; the orchestrator's own payloads are snake_case;
/// and a queue row written by one version of Charter has to stay readable by the next (section 2.3).
/// Reading both costs a few lines here and removes a class of bug that only shows up after a deploy.
/// </para>
/// </remarks>
public sealed record SpecBuildPayload
{
    /// <summary>The request whose thread this build belongs to.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>The specification to build. The session is derived from it.</summary>
    public required Guid SpecId { get; init; }

    /// <summary>
    /// Section 11's second button, when this build is a rebuild. <c>not_quite</c> means the requester
    /// tried the preview and it was not right, so this must become a <em>new</em> session on the same
    /// spec rather than resuming the one they just rejected.
    /// </summary>
    public string? Verdict { get; init; }

    /// <summary>True when this payload describes section 11's "not quite" rebuild.</summary>
    public bool IsRebuild => string.Equals(Verdict, "not_quite", StringComparison.Ordinal);

    /// <summary>
    /// Parses an approval-shaped payload, or returns null when the JSON is not one.
    /// </summary>
    /// <remarks>
    /// Hand-read rather than deserialised so that <c>requestId</c> and <c>request_id</c> are the same
    /// field. <see cref="JsonSerializerDefaults.Web"/> is case-insensitive but not
    /// separator-insensitive, so a single attribute would silently drop one of the two spellings.
    /// </remarks>
    public static SpecBuildPayload? TryParse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;

            if (ReadGuid(root, "specId", "spec_id") is not { } specId)
            {
                return null;
            }

            return new SpecBuildPayload
            {
                RequestId = ReadGuid(root, "requestId", "request_id") ?? Guid.Empty,
                SpecId = specId,
                Verdict = ReadString(root, "verdict", "verdict"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the payload in the spelling the orchestrator's own jobs use.</summary>
    public string ToJson()
    {
        var verdict = Verdict is null ? "null" : JsonSerializer.Serialize(Verdict);

        return $$"""
            {"request_id":"{{RequestId:D}}","spec_id":"{{SpecId:D}}","verdict":{{verdict}}}
            """;
    }

    /// <summary>
    /// The session id an approved specification builds under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function of its inputs, and that is the whole point. The control plane can restart
    /// between the spend gate opening and the session existing (section 2.3), so whoever picks the job
    /// up next must arrive at the <em>same</em> session id rather than minting a second one — the
    /// insert then collides on the primary key and the loser reads the winner's row.
    /// </para>
    /// <para>
    /// <paramref name="discriminator"/> is what separates section 11's rebuild from the original
    /// build. It is the feedback row's id, which is durable and written before the job is, so the
    /// derivation survives a restart exactly like the spec id does.
    /// </para>
    /// </remarks>
    public static Guid SessionIdFor(Guid specId, string? discriminator = null)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"charter.session:{specId:D}:{discriminator ?? string.Empty}"));

        var id = new byte[16];
        Array.Copy(bytes, id, 16);

        // Shaped as a name-based UUID (RFC 4122 version 5), like SessionJournal's event ids.
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id);
    }

    private static Guid? ReadGuid(JsonElement root, string camel, string snake)
        => ReadString(root, camel, snake) is { Length: > 0 } text && Guid.TryParse(text, out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement root, string camel, string snake)
    {
        foreach (var name in new[] { camel, snake })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
