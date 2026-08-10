using System.Globalization;

namespace Charter.Hubs;

/// <summary>
/// The two audiences a request streams to, as SignalR group names.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.4 applies to the live stream exactly as it applies to a <c>GET</c>: a requester must not
/// receive an engineer-only payload, and "the client will ignore the extra field" is not a
/// permission model. Splitting the group by audience is what makes that structural — a connection
/// only ever joins the group its
/// <see cref="Charter.Auth.Authorization.SessionVisibility"/> allows, and the publisher sends the
/// engineer-shaped frame to one group and the requester-shaped frame to the other.
/// </para>
/// <para>
/// Group membership is per connection and is rebuilt on reconnect, which is the only shape section
/// 2.3 permits: nothing about who is subscribed to what survives a container restart, because
/// nothing about it is authoritative.
/// </para>
/// </remarks>
public static class RequestStreamGroups
{
    /// <summary>The client method the hub invokes with each <c>RequestStreamEvent</c>.</summary>
    public const string EventMethod = "requestEvent";

    /// <summary>Everyone who may follow the thread but may not read the repository.</summary>
    public static string Requester(Guid requestId)
        => string.Create(CultureInfo.InvariantCulture, $"request:{requestId:N}:requester");

    /// <summary>Viewers with repository read access (section 7.4).</summary>
    public static string Engineer(Guid requestId)
        => string.Create(CultureInfo.InvariantCulture, $"request:{requestId:N}:engineer");
}
