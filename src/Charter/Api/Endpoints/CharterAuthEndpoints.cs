using System.Globalization;
using Charter.Api.Accounts;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Endpoints;

/// <summary>
/// The routes that let a human into Charter at all: first run, sign-in, sign-out, invitations and
/// password reset (sections 30.1, 30.2, 21).
/// </summary>
/// <remarks>
/// <para>
/// Two groups, and the split is the security property. The first is anonymous by necessity —
/// nobody can present a cookie before they have one — and every route in it carries
/// <see cref="CredentialRateLimitMetadata"/>, because each one either mints or consumes something
/// that grants access. The second requires the cookie like the rest of the API.
/// </para>
/// <para>
/// The handlers hold no rules, in the same way <see cref="CharterApiEndpoints"/>' do not. Whether a
/// token is spent, whether a link may be shown, and whether the two halves of a failed sign-in are
/// told apart are decided in <see cref="SetupService"/>, <see cref="AccountService"/> and
/// <see cref="SignInService"/>, where they can be tested without a server.
/// </para>
/// <para>
/// <b>Setup mode reaches these.</b> <c>SetupModeMiddleware</c> permits <c>/api/setup</c>, which is
/// why the redemption route lives there and not under <c>/api/auth</c> — an unclaimed instance must
/// answer exactly one route and it is this one.
/// </para>
/// </remarks>
public static class CharterAuthEndpoints
{
    /// <summary>Maps first run, sign-in, sign-out, invitations and password reset.</summary>
    public static IEndpointRouteBuilder MapCharterAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var open = endpoints
            .MapGroup("/api")
            .AllowAnonymous()
            .AddEndpointFilter<PlainLanguageFailureFilter>();

        var secure = endpoints
            .MapGroup("/api")
            .RequireAuthorization()
            .AddEndpointFilter<PlainLanguageFailureFilter>();

        MapFirstRun(open);
        MapSignIn(open);
        MapPasswords(open);
        MapInvitationAcceptance(open);
        MapSession(secure);
        MapAccountAdministration(secure);

        return endpoints;
    }

    /// <summary>Section 30.1: the one route an unclaimed instance answers.</summary>
    private static void MapFirstRun(IEndpointRouteBuilder open)
    {
        var firstRun = open.MapGroup("/setup");

        firstRun.MapGet("/status", async (SetupModeService mode, CancellationToken cancellationToken) =>
            Json(new SetupStatusResponse
            {
                SetupRequired = await mode.IsSetupRequiredAsync(cancellationToken),
            }));

        firstRun.MapPost("/complete", async (
                CompleteSetupBody body,
                HttpContext http,
                SetupService setup,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                body ??= new CompleteSetupBody();

                var result = await setup.CompleteAsync(
                    new SetupRequest
                    {
                        Token = body.Token ?? string.Empty,
                        Email = body.Email ?? string.Empty,
                        DisplayName = body.DisplayName ?? string.Empty,

                        // Wrapped immediately, so the plaintext never reaches a record whose
                        // ToString() somebody might interpolate into a log line.
                        Password = new Secret(body.Password ?? string.Empty),
                        OrganizationName = body.OrganizationName,
                    },
                    cancellationToken);

                if (result is SetupResult.Rejected rejected)
                {
                    // "Already set up" is a conflict rather than a 403: the instance state changed,
                    // and the person holding a token needs to be told to sign in instead.
                    return rejected.Reason == SetupRejection.AlreadyCompleted
                        ? ApiProblems.Conflict(rejected.Message)
                        : ApiProblems.BadRequest(rejected.Message);
                }

                var completed = (SetupResult.Completed)result;

                // The operator who just proved they can read the container log is signed in on the
                // spot; asking them to type the password they set one field ago is friction with no
                // security in it.
                var session = await signIn.ForUserAsync(
                    completed.UserId,
                    IdentityProviderKind.Password,
                    cancellationToken);

                return session is SignInResult.Succeeded succeeded
                    ? await IssueAsync(http, succeeded, StatusCodes.Status201Created)
                    : ApiProblems.Unexpected();
            })

            // Section 31: the one unauthenticated route that creates an administrator.
            .WithMetadata(CredentialRateLimitMetadata.Instance);
    }

    /// <summary>Section 21: email and password first, then whichever providers are configured.</summary>
    private static void MapSignIn(IEndpointRouteBuilder open)
    {
        var auth = open.MapGroup("/auth");

        auth.MapGet("/providers", (SignInService signIn, IAccountMailer mailer) =>
            Json(new AuthProvidersResponse
            {
                Providers =
                [
                    .. signIn.Available.Select(provider => new AuthProviderResponse
                    {
                        Name = provider.Name,
                        Style = provider.Style == IdentityProviderStyle.Redirect
                            ? ApiAuthProviderStyle.Redirect
                            : ApiAuthProviderStyle.Credential,
                        StartUrl = provider.Style == IdentityProviderStyle.Redirect
                            ? $"/api/auth/{provider.Name}/start"
                            : null,
                    }),
                ],
                // Change spec 001 part C.1: with email off there is no self-service reset, and the
                // page says who to ask instead of offering a button that cannot work.
                SelfServicePasswordReset = mailer.Availability.Enabled,
            }));

        auth.MapPost("/sign-in", async (
                SignInBody body,
                HttpContext http,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                body ??= new SignInBody();

                var result = await signIn.WithPasswordAsync(
                    body.Email,
                    body.Password is null ? null : new Secret(body.Password),
                    CharterRateLimiting.ClientKey(http),
                    cancellationToken);

                return result is SignInResult.Succeeded succeeded
                    ? await IssueAsync(http, succeeded, StatusCodes.Status200OK)
                    : Refuse(http, result);
            })
            .WithMetadata(CredentialRateLimitMetadata.Instance);

        auth.MapGet("/{provider}/start", async (
                string provider,
                string? returnUrl,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                var result = await signIn.BeginAsync(provider, ToUri(returnUrl), cancellationToken);

                return result is SignInResult.RedirectRequired redirect
                    ? Results.Redirect(redirect.Location.ToString())
                    : Describe(result);
            })
            .WithMetadata(CredentialRateLimitMetadata.Instance);

        auth.MapGet("/{provider}/callback", async (
                string provider,
                HttpContext http,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                var parameters = http.Request.Query.ToDictionary(
                    entry => entry.Key,
                    entry => (string?)entry.Value.ToString(),
                    StringComparer.Ordinal);

                var result = await signIn.CompleteAsync(
                    provider,
                    parameters,
                    CharterRateLimiting.ClientKey(http),
                    cancellationToken);

                if (result is not SignInResult.Succeeded succeeded)
                {
                    return Refuse(http, result);
                }

                await http.SignInAsync(CharterAuthenticationDefaults.Scheme, succeeded.Principal);

                // A browser arrives here, not fetch(), so the answer is a redirect into the app
                // rather than a body it would have no way to render.
                return Results.Redirect("/");
            })
            .WithMetadata(CredentialRateLimitMetadata.Instance);
    }

    /// <summary>Password reset, in its two halves (section 30.2, change spec 001 part C.1).</summary>
    private static void MapPasswords(IEndpointRouteBuilder open)
    {
        var auth = open.MapGroup("/auth");

        auth.MapPost("/forgot-password", async (
                ForgotPasswordBody body,
                AccountService accounts,
                CancellationToken cancellationToken) =>

                // Always 202, always the same sentence, never a link. Anybody can type anybody's
                // address into this form.
                Results.Json(
                    await accounts.ForgotPasswordAsync(body ?? new ForgotPasswordBody(), cancellationToken),
                    CharterApiJson.Options,
                    statusCode: StatusCodes.Status202Accepted))
            .WithMetadata(CredentialRateLimitMetadata.Instance);

        auth.MapPost("/reset-password", async (
                ResetPasswordBody body,
                AccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (outcome, _) = await accounts.ResetPasswordAsync(
                    body ?? new ResetPasswordBody(),
                    cancellationToken);

                // No session is issued here. Proving control of a mailbox is enough to set a
                // password and not enough to be handed a signed-in browser.
                return CharterApiEndpoints.ToResult(outcome);
            })
            .WithMetadata(CredentialRateLimitMetadata.Instance);
    }

    /// <summary>Section 30.2: accepting an invitation is what creates an account.</summary>
    private static void MapInvitationAcceptance(IEndpointRouteBuilder open)
        => open.MapPost("/auth/invitations/accept", async (
                AcceptInvitationBody body,
                HttpContext http,
                AccountService accounts,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                var (outcome, userId) = await accounts.AcceptInvitationAsync(
                    body ?? new AcceptInvitationBody(),
                    cancellationToken);

                if (!outcome.Succeeded)
                {
                    return CharterApiEndpoints.ToResult(outcome);
                }

                var session = await signIn.ForUserAsync(
                    userId,
                    IdentityProviderKind.Password,
                    cancellationToken);

                return session is SignInResult.Succeeded succeeded
                    ? await IssueAsync(http, succeeded, StatusCodes.Status201Created)
                    : ApiProblems.Unexpected();
            })
            .WithMetadata(CredentialRateLimitMetadata.Instance);

    /// <summary>Who is signed in, and how to stop being.</summary>
    private static void MapSession(IEndpointRouteBuilder secure)
    {
        var auth = secure.MapGroup("/auth");

        auth.MapGet("/session", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            CharterDbContext database,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var user = await database.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == member.UserId, cancellationToken);

            if (user is null)
            {
                return ApiProblems.Unauthorized();
            }

            return Json(new SessionResponse
            {
                UserId = user.Id.ToString(),
                DisplayName = user.DisplayName,
                Email = user.Email,
                OrganizationId = member.OrgId.ToString(),
                Roles = [.. member.Roles.Select(role => role.ToApi())],
                Provider = http.User.FindFirst(CharterClaimTypes.Provider)?.Value ?? "password",
            });
        });

        auth.MapPost("/sign-out", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            SignInService signIn,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);

            // The cookie goes whatever the membership says. A session whose member row has been
            // deleted must still be able to sign out.
            await http.SignOutAsync(CharterAuthenticationDefaults.Scheme);

            if (member is not null)
            {
                await signIn.RecordSignOutAsync(member, cancellationToken);
            }

            return Results.NoContent();
        });
    }

    /// <summary>Section 30.2's <em>invite people</em>, and the admin-side reset (part C.1).</summary>
    private static void MapAccountAdministration(IEndpointRouteBuilder secure)
    {
        var invitations = secure.MapGroup("/invitations");

        invitations.MapGet("/", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            AccountService accounts,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, listed) = await accounts.ListInvitationsAsync(member, cancellationToken);

            return outcome.Succeeded && listed is not null ? Json(listed) : CharterApiEndpoints.ToResult(outcome);
        });

        invitations.MapPost("/", async (
                InviteMemberBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                AccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
                if (member is null)
                {
                    return ApiProblems.Unauthorized();
                }

                var (outcome, issued) = await accounts.InviteAsync(
                    member,
                    body ?? new InviteMemberBody(),
                    cancellationToken);

                // The link, when there is one, is in the body of the response that created it and
                // nowhere else. There is no endpoint that reads it back.
                return outcome.Succeeded && issued is not null
                    ? Results.Json(issued, CharterApiJson.Options, statusCode: StatusCodes.Status201Created)
                    : CharterApiEndpoints.ToResult(outcome);
            })

            // An invitation is a message to a real inbox, so it is limited like intake (section 31).
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        invitations.MapDelete("/{id}", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            AccountService accounts,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            return Guid.TryParse(id, CultureInfo.InvariantCulture, out var invitationId)
                ? CharterApiEndpoints.ToResult(
                    await accounts.RevokeInvitationAsync(member, invitationId, cancellationToken))
                : ApiProblems.NotFound();
        });

        secure.MapPost("/password-resets", async (
                AdminPasswordResetBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                AccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
                if (member is null)
                {
                    return ApiProblems.Unauthorized();
                }

                var (outcome, link) = await accounts.AdminPasswordResetAsync(
                    member,
                    body ?? new AdminPasswordResetBody(),
                    cancellationToken);

                return outcome.Succeeded && link is not null ? Json(link) : CharterApiEndpoints.ToResult(outcome);
            })
            .WithMetadata(IntakeRateLimitMetadata.Instance);
    }

    /// <summary>Issues the cookie and answers with the session.</summary>
    private static async Task<IResult> IssueAsync(HttpContext http, SignInResult.Succeeded succeeded, int status)
    {
        await http.SignInAsync(CharterAuthenticationDefaults.Scheme, succeeded.Principal);

        return Results.Json(
            new SessionResponse
            {
                UserId = succeeded.Member.UserId.ToString(),
                DisplayName = succeeded.DisplayName,
                Email = succeeded.Email,
                OrganizationId = succeeded.Member.OrgId.ToString(),
                Roles = [.. succeeded.Member.Roles.Select(role => role.ToApi())],
                Provider = succeeded.Provider.ToString().ToLowerInvariant(),
            },
            CharterApiJson.Options,
            statusCode: status);
    }

    /// <summary>
    /// Turns a refusal into a status, adding <c>Retry-After</c> when the throttle asked for one.
    /// </summary>
    private static IResult Refuse(HttpContext http, SignInResult result)
    {
        if (result is SignInResult.Failed { RetryAfter: { } retryAfter })
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return Describe(result);
    }

    private static IResult Describe(SignInResult result) => result switch
    {
        SignInResult.Failed { Reason: SignInFailure.Throttled } throttled =>
            Results.Problem(
                title: "Too many attempts",
                detail: throttled.Message,
                statusCode: StatusCodes.Status429TooManyRequests),

        SignInResult.Failed { Reason: SignInFailure.UnknownProvider } unknown =>
            ApiProblems.BadRequest(unknown.Message),

        SignInResult.Failed { Reason: SignInFailure.Malformed } malformed =>
            ApiProblems.BadRequest(malformed.Message),

        // Everything else is 401 with the provider's own sentence. "No such user" and "wrong
        // password" arrive here as the same value carrying the same words (section 21).
        SignInResult.Failed failed => Results.Problem(
            title: "We could not sign you in",
            detail: failed.Message,
            statusCode: StatusCodes.Status401Unauthorized),

        _ => ApiProblems.Unexpected(),
    };

    private static Uri? ToUri(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var parsed) ? parsed : null;

    private static IResult Json<T>(T value) => Results.Json(value, CharterApiJson.Options);
}
