using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Agent.Protocol;

/// <summary>
/// Every frame on the wire is one of these, serialised as a single JSON text frame.
/// </summary>
/// <remarks>
/// <code>
/// { "v": 1, "type": "job.claim", "id": "018f...", "correlationId": null,
///   "sentAt": "2026-08-10T09:12:44.113Z", "payload": { ... } }
/// </code>
/// The payload is left as a <see cref="JsonElement"/> so an unknown message type can be logged and
/// ignored rather than failing the connection — a control plane one minor version ahead may send
/// frames this build has no record for.
/// </remarks>
public sealed record Envelope
{
    /// <summary>Protocol version of this frame. Always <see cref="AgentProtocol.Version"/> from the agent.</summary>
    [JsonPropertyName("v")]
    public required int ProtocolVersion { get; init; }

    /// <summary>One of <see cref="MessageTypes"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Unique per frame. Used as the correlation target for a reply.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The <see cref="Id"/> of the frame this one answers, when it answers one.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("sentAt")]
    public required DateTimeOffset SentAt { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    public static Envelope Create<T>(string type, T payload, DateTimeOffset now, string? correlationId = null)
        where T : notnull =>
        new()
        {
            ProtocolVersion = AgentProtocol.Version,
            Type = type,
            Id = Guid.CreateVersion7().ToString("n"),
            CorrelationId = correlationId,
            SentAt = now,
            Payload = JsonSerializer.SerializeToElement(payload, AgentJson.Options),
        };

    /// <summary>Reads the payload as <typeparamref name="T"/>, or null when there is no payload.</summary>
    public T? ReadPayload<T>()
        where T : class =>
        Payload is null || Payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : Payload.Value.Deserialize<T>(AgentJson.Options);

    public string ToJson() => JsonSerializer.Serialize(this, AgentJson.Options);

    public static Envelope? FromJson(string json) =>
        JsonSerializer.Deserialize<Envelope>(json, AgentJson.Options);
}

/// <summary>Serialisation settings shared by every frame and the pairing call.</summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
