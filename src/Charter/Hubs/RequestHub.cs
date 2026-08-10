using System.Globalization;
using Charter.Api;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Charter.Hubs;

/// <summary>
/// The live stream behind <c>subscribeToRequest</c> — SignalR at <c>/hub/requests</c>, grouped per
/// request.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Authorisation is the same code path as the REST layer.</strong> A subscription resolves
/// the member through <see cref="CharterCaller"/> and asks
/// <see cref="RequestQueryService.LoadAsync"/> the identical question a <c>GET</c> asks, so a
/// connection can never be granted something the corresponding fetch would refuse. There is no
/// hub-specific rule here to drift from the endpoint's.
/// </para>
/// <para>
/// The audience the connection lands in follows
/// <see cref="Charter.Auth.Authorization.SessionVisibility.Transcript"/> — the one gate section 7.4
/// names — so a requester joins a group that engineer-shaped frames are never sent to. Nothing is
/// filtered on the client.
/// </para>
/// <para>
/// Section 2.3: a hub connection holds no state worth keeping. Reconnecting is refetching the detail
/// and resubscribing, and every replayed frame is idempotent by its content-derived id.
/// </para>
/// </remarks>
[Authorize]
public sealed class RequestHub : Hub
{
    private readonly ICharterAuthorizationService authorization;
    private readonly RequestQueryService requests;

    public RequestHub(ICharterAuthorizationService authorization, RequestQueryService requests)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(requests);

        this.authorization = authorization;
        this.requests = requests;
    }

    /// <summary>
    /// Joins the caller to the audience group for one request.
    /// </summary>
    /// <returns>
    /// The group joined, so a client can assert what it was given rather than infer it.
    /// </returns>
    /// <exception cref="HubException">
    /// The id is not a request this caller may follow. The message is plain language and identical
    /// for "no such request" and "not yours", so the hub is no more of an existence oracle than the
    /// REST layer is (section 7.3).
    /// </exception>
    public async Task<string> Subscribe(string requestId)
    {
        var view = await AuthorizeAsync(requestId);
        var group = view.Visibility.Transcript
            ? RequestStreamGroups.Engineer(view.Aggregate.Request.Id)
            : RequestStreamGroups.Requester(view.Aggregate.Request.Id);

        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        return group;
    }

    /// <summary>Leaves both audience groups for a request. Safe to call without having subscribed.</summary>
    public async Task Unsubscribe(string requestId)
    {
        if (!Guid.TryParse(requestId, CultureInfo.InvariantCulture, out var id))
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RequestStreamGroups.Requester(id));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RequestStreamGroups.Engineer(id));
    }

    private async Task<RequestView> AuthorizeAsync(string requestId)
    {
        if (!Guid.TryParse(requestId, CultureInfo.InvariantCulture, out var id))
        {
            throw new HubException(SubscriptionRefused);
        }

        var member = await CharterCaller.ResolveAsync(Context.User, authorization, Context.ConnectionAborted);
        if (member is null)
        {
            throw new HubException("You are not signed in. Sign in again and we will bring you back here.");
        }

        var view = await requests.LoadAsync(member, id, Context.ConnectionAborted);

        return view ?? throw new HubException(SubscriptionRefused);
    }

    /// <summary>One sentence for "gone" and "not yours" alike.</summary>
    internal const string SubscriptionRefused =
        "We could not find that request. It may have been removed, or it may belong to someone else.";
}
