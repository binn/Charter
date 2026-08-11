using System.Globalization;
using Charter.Api;
using Charter.Api.Endpoints;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Charter.Storage;

/// <summary>
/// <c>GET /api/requests/{id}/blobs/{key}</c> — stored bytes, behind the same permission as pane 2
/// (sections 7.4, 12, 27.7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a proxy and not a link.</strong> The obvious way to serve an object is to hand out its
/// URL, and it is wrong here. A public bucket URL authorises nobody; a presigned URL authorises
/// whoever holds it. Either one turns "this artifact is engineer-only" into "this artifact is
/// engineer-only until somebody pastes the link", and no request reaches Charter to refuse - which is
/// precisely what section 7.4 forbids. So bytes come back through Charter, and the permission asked
/// is <see cref="SessionVisibility.Transcript"/>: the same question <c>GET /api/requests/{id}</c>
/// asks before it carries a transcript at all, and the same one the transcript endpoint asks before
/// it returns a page. A requester who may not read pane 2 may not read what pane 2 points at either.
/// </para>
/// <para>
/// <strong>The key is untrusted (section 16).</strong> It arrives in a URL and is never used to build
/// a path until it has been checked against the session the caller was authorised for. That check is
/// the load-bearing one: whatever the key says, it can only ever address objects under the session of
/// the request whose permission was just granted, so a key copied out of one organisation's
/// transcript is a 404 in another's.
/// </para>
/// <para>
/// <strong>Nothing here renders.</strong> The content type is decided from the key Charter chose, not
/// from what the store reports, the response is an attachment, and <c>X-Content-Type-Options</c> is
/// set — so an object cannot be talked into executing as HTML in a viewer's session.
/// </para>
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>The route stored bytes are served from.</summary>
    public const string RoutePattern = "/{id}/blobs/{**key}";

    /// <summary>Maps the authorised read. Call after authentication middleware.</summary>
    public static IEndpointRouteBuilder MapCharterStorageBlobs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var requests = endpoints
            .MapGroup("/api/requests")
            .RequireAuthorization()
            .AddEndpointFilter<PlainLanguageFailureFilter>();

        requests.MapGet(RoutePattern, ReadAsync);

        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        string id,
        string key,
        HttpContext http,
        ICharterAuthorizationService authorization,
        RequestQueryService requests,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
        if (member is null)
        {
            return ApiProblems.Unauthorized();
        }

        if (!Guid.TryParse(id, CultureInfo.InvariantCulture, out var requestId))
        {
            return ApiProblems.NotFound();
        }

        var view = await requests.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return ApiProblems.NotFound();
        }

        if (!view.Visibility.Transcript)
        {
            return ApiProblems.Forbidden("viewing stored session output needs read access to the repository");
        }

        if (view.Aggregate.Session is not { } session)
        {
            return ApiProblems.NotFound();
        }

        if (!ObjectKey.IsWithinSession(key, session.Id))
        {
            // Indistinguishable from an object that is not there, deliberately (section 7.3): a
            // different answer for "that key is another session's" would make this an oracle for what
            // other sessions have stored.
            return ApiProblems.NotFound();
        }

        if (!store.Enabled)
        {
            return ApiProblems.Unavailable(
                "This instance keeps session output in its database rather than in object storage, so "
                + "there is nothing stored to fetch.");
        }

        var content = await store.GetAsync(key, cancellationToken);
        if (content is null)
        {
            return ApiProblems.NotFound();
        }

        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";

        return Results.File(content.Bytes, content.ContentType, ObjectKey.FileName(key));
    }
}
