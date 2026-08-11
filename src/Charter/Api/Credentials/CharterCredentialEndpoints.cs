using System.Globalization;
using Charter.Auth.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Charter.Api.Credentials;

/// <summary>
/// <c>/api/credentials</c>: the routes that link, list and revoke model credentials (section 20b.2).
/// </summary>
/// <remarks>
/// <para>
/// The absence of these routes was half the defect they exist for. The section 20b.3 chain resolves
/// against <c>credential_grants</c>, and nothing in the application could create a row in it — the
/// only working procedure was to encrypt a key by hand and <c>INSERT</c> it, which no operator will
/// ever discover from the documentation.
/// </para>
/// <para>
/// Every handler follows the same three steps as the rest of the API: resolve the acting member from
/// the cookie by re-reading the <c>members</c> row, ask <see cref="CredentialsService"/> what that
/// member may do, and serialise through <see cref="CharterApiJson"/>. No rule lives in a lambda here.
/// </para>
/// </remarks>
public static class CharterCredentialEndpoints
{
    /// <summary>Maps the three credential routes under an already-authorised <c>/api</c> group.</summary>
    public static IEndpointRouteBuilder MapCharterCredentialEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var credentials = api.MapGroup("/credentials");

        credentials.MapGet("/", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            CredentialsService service,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, list) = await service.ListAsync(member, cancellationToken);

            return outcome.Succeeded && list is not null
                ? Results.Json(list, CharterApiJson.Options)
                : ToResult(outcome);
        });

        credentials.MapPost("/", async (
            CreateCredentialBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            CredentialsService service,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, created) = await service.CreateAsync(
                member,
                body ?? new CreateCredentialBody(),
                cancellationToken);

            return outcome.Succeeded && created is not null
                ? Results.Json(created, CharterApiJson.Options, statusCode: StatusCodes.Status201Created)
                : ToResult(outcome);
        });

        // POST rather than DELETE: revocation is a state transition that leaves the row behind
        // (section 20b.2), and the audit trail reads better for a verb than for an absence.
        credentials.MapPost("/{id}/revoke", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            CredentialsService service,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            // An instance-level credential has an id too, and it is not revocable over HTTP: it is an
            // environment variable, and pretending otherwise would answer 204 to a call that changed
            // nothing. Section 7.3's "gone or never yours" answer is the honest one.
            if (!Guid.TryParse(id, CultureInfo.InvariantCulture, out var credentialId))
            {
                return ApiProblems.NotFound();
            }

            return ToResult(await service.RevokeAsync(member, credentialId, cancellationToken));
        });

        return api;
    }

    private static IResult ToResult(Charter.Api.Requests.CommandOutcome outcome) => outcome.Status switch
    {
        StatusCodes.Status204NoContent => Results.NoContent(),
        StatusCodes.Status400BadRequest => ApiProblems.BadRequest(outcome.Reason),
        StatusCodes.Status403Forbidden => ApiProblems.Forbidden(outcome.Reason),
        StatusCodes.Status404NotFound => ApiProblems.NotFound(),
        StatusCodes.Status409Conflict => ApiProblems.Conflict(outcome.Reason),
        _ => ApiProblems.Unexpected(),
    };
}
