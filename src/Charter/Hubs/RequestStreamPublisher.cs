using Charter.Api.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Charter.Hubs;

/// <summary>
/// Pushes <see cref="RequestStreamEvent"/> frames to the two audiences of a request.
/// </summary>
/// <remarks>
/// Section 11 requires that <em>something</em> streams — "a 5–20 minute silent gap reads as broken" —
/// and section 2.3 requires that the stream never be the source of truth. Both hold here: every frame
/// this publishes is also derivable from a subsequent <c>GET</c>, and every frame is idempotent by
/// its content-derived <see cref="RequestStreamEvent.Id"/>.
/// </remarks>
public interface IRequestStreamPublisher
{
    /// <summary>Sends one frame to everybody following the request, whatever their access.</summary>
    Task PublishAsync(Guid requestId, RequestStreamEvent frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a differentiated pair: <paramref name="requesterFrame"/> to viewers without repository
    /// read, <paramref name="engineerFrame"/> to viewers with it (section 7.4).
    /// </summary>
    Task PublishAsync(
        Guid requestId,
        RequestStreamEvent requesterFrame,
        RequestStreamEvent engineerFrame,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RequestStreamPublisher : IRequestStreamPublisher
{
    private readonly IHubContext<RequestHub> hub;

    public RequestStreamPublisher(IHubContext<RequestHub> hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        this.hub = hub;
    }

    /// <inheritdoc />
    public Task PublishAsync(
        Guid requestId,
        RequestStreamEvent frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return PublishAsync(requestId, frame, frame, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        Guid requestId,
        RequestStreamEvent requesterFrame,
        RequestStreamEvent engineerFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requesterFrame);
        ArgumentNullException.ThrowIfNull(engineerFrame);

        await hub.Clients
            .Group(RequestStreamGroups.Requester(requestId))
            .SendAsync(RequestStreamGroups.EventMethod, requesterFrame, cancellationToken);

        await hub.Clients
            .Group(RequestStreamGroups.Engineer(requestId))
            .SendAsync(RequestStreamGroups.EventMethod, engineerFrame, cancellationToken);
    }
}
