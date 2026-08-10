using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Api;

/// <summary>
/// The one serialiser the HTTP API and the SignalR hub both write through.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> is the mechanism section 7.4 rests on. A field
/// the viewer may not see is projected as <c>null</c> and therefore <em>does not appear</em> in the
/// response body at all — not <c>null</c>, not <c>[]</c>, absent. The client's test for "may I see
/// this" is `'details' in artifact`, and that only works if the server never writes the key.
/// </para>
/// <para>
/// These options are local to the API rather than installed globally, so nothing else in the process
/// silently changes shape, and the hub can reuse the identical spelling by copying them.
/// </para>
/// </remarks>
public static class CharterApiJson
{
    /// <summary>Web defaults (camelCase), snake_case enum names, null properties omitted.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(freeze: true);

    /// <summary>
    /// A fresh, writable copy for consumers that insist on owning their instance — SignalR's JSON
    /// protocol, for one.
    /// </summary>
    public static JsonSerializerOptions CreateWritableCopy() => CreateOptions(freeze: false);

    private static JsonSerializerOptions CreateOptions(bool freeze)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Section 7.4. Omission is the authorisation mechanism; see the remarks above.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // The SPA's unions are spelled `spec_ready`, `hosted_preview`, `explain_everything`.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        if (freeze)
        {
            // `populateMissingResolver` fills in the default reflection-based resolver; the
            // parameterless overload throws when none has been configured, which is exactly the case
            // for a hand-built options instance.
            options.MakeReadOnly(populateMissingResolver: true);
        }

        return options;
    }
}
