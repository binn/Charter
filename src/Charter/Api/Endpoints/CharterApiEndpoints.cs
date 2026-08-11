using System.Globalization;
using Charter.Api.Changes;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Api.Runners;
using Charter.Api.Settings;
using Charter.Api.Setup;
using Charter.Api.Viewer;
using Charter.Auth.Authorization;
using Charter.Domain;
using Microsoft.AspNetCore.Routing;

namespace Charter.Api.Endpoints;

/// <summary>
/// The routes <c>ClientApp/src/api/client.ts</c> declares, one for one.
/// </summary>
/// <remarks>
/// <para>
/// Every handler follows the same three steps: resolve the acting member from the cookie by re-reading
/// the <c>members</c> row, ask a service what that member may be shown or may do, and serialise
/// through <see cref="CharterApiJson"/> so anything withheld is absent rather than null.
/// </para>
/// <para>
/// The handlers hold no rules. A rule in a lambda is a rule that cannot be unit tested and cannot be
/// found by somebody auditing section 7.4, so the lambdas here are plumbing and the judgement lives
/// in <see cref="RequestQueryService"/>, <see cref="RequestCommandService"/> and the policies in
/// <c>Charter.Auth.Authorization</c>.
/// </para>
/// </remarks>
public static class CharterApiEndpoints
{
    /// <summary>Maps every route under <c>/api</c> that the SPA calls.</summary>
    public static IEndpointRouteBuilder MapCharterApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints
            .MapGroup("/api")
            .RequireAuthorization()

            // Section 11: whatever goes wrong, the person reading it gets a sentence rather than a
            // stack trace.
            .AddEndpointFilter<PlainLanguageFailureFilter>();

        // Sections 30.1, 30.2 and 21: the routes a human uses to get in at all. Mapped from here so
        // there is one call the host makes, but in their own groups — most of them are anonymous by
        // necessity and must not inherit this group's RequireAuthorization.
        endpoints.MapCharterAuthEndpoints();

        // Section 9: connect, recon, confirm scope, smoke test, primer.
        endpoints.MapCharterOnboardingEndpoints();

        MapViewer(api);
        MapProjects(api);
        MapRequests(api);
        MapSessionActions(api);
        MapApprovals(api);
        MapRunners(api);
        MapSetup(api);
        MapSettings(api);

        return endpoints;
    }

    private static void MapViewer(IEndpointRouteBuilder api)
    {
        api.MapGet("/me", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            ViewerService viewers,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var viewer = await viewers.DescribeAsync(member, cancellationToken);
            return viewer is null ? ApiProblems.Unauthorized() : Json(viewer);
        });

        api.MapPatch("/me/preferences", async (
            UpdatePreferencesBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            ViewerService viewers,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var resolved = await viewers.UpdatePreferencesAsync(member, body ?? new UpdatePreferencesBody(), cancellationToken);

            return Json(new UserPreferencesResponse
            {
                Theme = resolved.Theme,
                Pane = ViewerService.PaneFor(member, resolved),
                TeachingLevel = resolved.TeachingLevel,
            });
        });

        api.MapPost("/me/onboarding/requester/complete", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            ViewerService viewers,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            await viewers.CompleteRequesterOnboardingAsync(member, cancellationToken);

            var viewer = await viewers.DescribeAsync(member, cancellationToken);
            return viewer is null ? ApiProblems.Unauthorized() : Json(viewer);
        });
    }

    private static void MapProjects(IEndpointRouteBuilder api)
        => api.MapGet("/projects", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            return Json(await queries.ListProjectsAsync(member, cancellationToken));
        });

    private static void MapApprovals(IEndpointRouteBuilder api)
        => api.MapGet("/approvals", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            return Json(await queries.ListPendingApprovalsAsync(member, cancellationToken));
        });

    private static void MapRequests(IEndpointRouteBuilder api)
    {
        var requests = api.MapGroup("/requests");

        requests.MapGet("/", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            return Json(await queries.ListRequestsAsync(member, cancellationToken));
        });

        requests.MapGet("/{id}", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            if (!Guid.TryParse(id, CultureInfo.InvariantCulture, out var requestId))
            {
                return ApiProblems.NotFound();
            }

            var view = await queries.LoadAsync(member, requestId, cancellationToken);

            // Section 7.3: "not yours" and "does not exist" are the same answer, so the API is never
            // an existence oracle for another organisation's work.
            return view is null
                ? ApiProblems.NotFound()
                : Json(RequestProjection.Detail(view.Aggregate, view.Visibility, queries.Now()));
        });

        // Section 31: intake is the one endpoint a script can use to queue four hundred sessions, so
        // it is the one carrying the per-user and per-organisation limiter.
        requests.MapPost("/", async (
                CreateRequestBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RequestQueryService queries,
                RequestCommandService commands,
                CancellationToken cancellationToken) =>
            {
                var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
                if (member is null)
                {
                    return ApiProblems.Unauthorized();
                }

                var (outcome, requestId) = await commands.CreateAsync(
                    member,
                    body ?? new CreateRequestBody(),
                    cancellationToken);

                if (!outcome.Succeeded)
                {
                    return ToResult(outcome);
                }

                var view = await queries.LoadAsync(member, requestId, cancellationToken);

                return view is null
                    ? ApiProblems.NotFound()
                    : Results.Json(
                        RequestProjection.Detail(view.Aggregate, view.Visibility, queries.Now()),
                        CharterApiJson.Options,
                        statusCode: StatusCodes.Status201Created);
            })
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        requests.MapPost("/{id}/refinement", async (
                string id,
                SendRefinementMessageBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RequestCommandService commands,
                CancellationToken cancellationToken) =>
            {
                var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                return ToResult(await commands.SendRefinementMessageAsync(
                    member!,
                    requestId,
                    body ?? new SendRefinementMessageBody(),
                    cancellationToken));
            })

            // A scripted refinement loop spends model tokens just as intake does (section 31).
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        requests.MapPost("/{id}/spec/{version:int}/approve", async (
            string id,
            int version,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.ApproveSpecAsync(member!, requestId, version, cancellationToken));
        });

        requests.MapPost("/{id}/spec/{version:int}/changes-requested", async (
            string id,
            int version,
            RequestSpecChangesBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.RequestSpecChangesAsync(
                member!,
                requestId,
                version,
                body ?? new RequestSpecChangesBody(),
                cancellationToken));
        });

        requests.MapPost("/{id}/feedback", async (
            string id,
            SubmitFeedbackBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.SubmitFeedbackAsync(
                member!,
                requestId,
                body ?? new SubmitFeedbackBody(),
                cancellationToken));
        });

        requests.MapPost("/{id}/cancel", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.CancelAsync(member!, requestId, cancellationToken));
        });

        requests.MapPost("/{id}/artifacts/{artifactId}/rebuild", async (
                string id,
                string artifactId,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RequestCommandService commands,
                CancellationToken cancellationToken) =>
            {
                var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                if (!Guid.TryParse(artifactId, CultureInfo.InvariantCulture, out var artifact))
                {
                    return ApiProblems.NotFound();
                }

                return ToResult(await commands.RebuildArtifactAsync(member!, requestId, artifact, cancellationToken));
            })

            // Rebuilding runs a session. Section 31 counts it as intake.
            .WithMetadata(IntakeRateLimitMetadata.Instance);
    }

    /// <summary>Panes 2 and 3 (section 12), and section 7.5's four post-hoc actions.</summary>
    private static void MapSessionActions(IEndpointRouteBuilder api)
    {
        var requests = api.MapGroup("/requests");

        requests.MapGet("/{id}/transcript", async (
            string id,
            string? cursor,
            long? aroundSeq,
            int? limit,
            HttpContext http,
            ICharterAuthorizationService authorization,
            TranscriptQueryService transcripts,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var read = await transcripts.ReadAsync(
                member!,
                requestId,
                new TranscriptWindow(cursor, aroundSeq, limit),
                cancellationToken);

            return read.Status switch
            {
                TranscriptReadStatus.Ok => Json(read.Page),

                // Section 7.4, in the shape a fetch has. The same rule that keeps `transcript` out of
                // the detail body keeps the page out of this one.
                TranscriptReadStatus.Forbidden => ApiProblems.Forbidden(read.Reason),
                _ => ApiProblems.NotFound(),
            };
        });

        requests.MapGet("/{id}/changes/{*path}", async (
            string id,
            string path,
            HttpContext http,
            ICharterAuthorizationService authorization,
            FileDiffService diffs,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var read = await diffs.ReadAsync(member!, requestId, path, cancellationToken);

            return read.Status switch
            {
                FileDiffReadStatus.Ok => Json(read.Diff),
                FileDiffReadStatus.Forbidden => ApiProblems.Forbidden(read.Reason),
                FileDiffReadStatus.Unavailable => ApiProblems.Unavailable(read.Reason),
                _ => ApiProblems.NotFound(),
            };
        });

        requests.MapPost("/{id}/session/approve", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.ApproveSessionAsync(member!, requestId, cancellationToken));
        });

        requests.MapPost("/{id}/session/steer", async (
                string id,
                SteerSessionBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RequestCommandService commands,
                CancellationToken cancellationToken) =>
            {
                var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                return ToResult(await commands.SteerSessionAsync(
                    member!,
                    requestId,
                    body ?? new SteerSessionBody(),
                    cancellationToken));
            })

            // Steering spends model tokens exactly as intake does (section 31).
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        requests.MapPost("/{id}/session/revise", async (
                string id,
                ReviseSessionBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RequestCommandService commands,
                CancellationToken cancellationToken) =>
            {
                var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                return ToResult(await commands.ReviseSessionAsync(
                    member!,
                    requestId,
                    body ?? new ReviseSessionBody(),
                    cancellationToken));
            })
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        requests.MapPost("/{id}/session/take-over", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RequestCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var (member, requestId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return ToResult(await commands.TakeOverSessionAsync(member!, requestId, cancellationToken));
        });
    }

    /// <summary>Settings → Runners (section 33.3).</summary>
    private static void MapRunners(IEndpointRouteBuilder api)
    {
        var runners = api.MapGroup("/runners");

        runners.MapGet("/", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RunnersQueryService query,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            if (!member.HasRole(MemberRole.Admin))
            {
                return ApiProblems.Forbidden("the runners list belongs to administrators");
            }

            return Json(await query.DescribeAsync(member, cancellationToken));
        });

        runners.MapPost("/pairing-tokens", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RunnersCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, token) = await commands.IssuePairingTokenAsync(member, cancellationToken);

            // Section 33.3: shown exactly once, so it is the body of the response that created it and
            // there is no second endpoint that reads it back.
            return outcome.Succeeded && token is not null
                ? Results.Json(token, CharterApiJson.Options, statusCode: StatusCodes.Status201Created)
                : ToResult(outcome);
        });

        runners.MapDelete("/{agentId}", async (
            string agentId,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RunnersCommandService commands,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            if (!Guid.TryParse(agentId, CultureInfo.InvariantCulture, out var id))
            {
                return ApiProblems.NotFound();
            }

            return ToResult(await commands.RevokeAsync(member, id, cancellationToken));
        });
    }

    /// <summary>The admin setup checklist (section 30.2).</summary>
    private static void MapSetup(IEndpointRouteBuilder api)
    {
        var setup = api.MapGroup("/setup");

        setup.MapGet("/checklist", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            SetupChecklistService checklist,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            // Null, not 403: a requester's dashboard has no checklist on it, and the client resolves
            // this to `SetupChecklist | null` rather than to an error it would have to render.
            return CharterApiJson.NullOr(await checklist.DescribeAsync(member, cancellationToken));
        });

        setup.MapPost("/checklist/dismiss", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            SetupChecklistService checklist,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, dismissed) = await checklist.DismissAsync(member, cancellationToken);

            return outcome.Succeeded && dismissed is not null ? Json(dismissed) : ToResult(outcome);
        });
    }

    /// <summary>Admin settings: email availability and the test send (change spec 001 part C.3).</summary>
    private static void MapSettings(IEndpointRouteBuilder api)
    {
        var settings = api.MapGroup("/settings");

        settings.MapGet("/email", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            EmailSettingsService email,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, described) = await email.DescribeAsync(member, cancellationToken);

            return outcome.Succeeded && described is not null ? Json(described) : ToResult(outcome);
        });

        settings.MapPost("/email/test", async (
                SendTestEmailBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                EmailSettingsService email,
                CancellationToken cancellationToken) =>
            {
                var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
                if (member is null)
                {
                    return ApiProblems.Unauthorized();
                }

                var (outcome, result) = await email.SendTestAsync(
                    member,
                    body ?? new SendTestEmailBody(),
                    cancellationToken);

                // A mail server that refused the message is a 200 carrying `sent: false` and the
                // server's own words. The only non-2xx here is "you are not an administrator".
                return outcome.Succeeded && result is not null ? Json(result) : ToResult(outcome);
            })

            // A test send is a message to a real inbox, so it is limited like intake (section 31).
            .WithMetadata(IntakeRateLimitMetadata.Instance);
    }

    private static async Task<(MemberSnapshot? Member, Guid RequestId, IResult? Failure)> ResolveAsync(
        HttpContext http,
        ICharterAuthorizationService authorization,
        string id,
        CancellationToken cancellationToken)
    {
        var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
        if (member is null)
        {
            return (null, Guid.Empty, ApiProblems.Unauthorized());
        }

        return Guid.TryParse(id, CultureInfo.InvariantCulture, out var requestId)
            ? (member, requestId, null)
            : (null, Guid.Empty, ApiProblems.NotFound());
    }

    private static IResult Json<T>(T value) => Results.Json(value, CharterApiJson.Options);

    /// <summary>Turns a command outcome into the response the client's <c>ApiError</c> expects.</summary>
    internal static IResult ToResult(CommandOutcome outcome) => outcome.Status switch
    {
        StatusCodes.Status204NoContent => Results.NoContent(),
        StatusCodes.Status400BadRequest => ApiProblems.BadRequest(outcome.Reason),
        StatusCodes.Status403Forbidden => ApiProblems.Forbidden(outcome.Reason),
        StatusCodes.Status404NotFound => ApiProblems.NotFound(),
        StatusCodes.Status409Conflict => ApiProblems.Conflict(outcome.Reason),
        _ => ApiProblems.Unexpected(),
    };
}
