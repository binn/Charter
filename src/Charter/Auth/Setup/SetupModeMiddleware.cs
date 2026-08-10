using Microsoft.AspNetCore.Http;

namespace Charter.Auth.Setup;

/// <summary>
/// While the instance has no users, serves nothing but the setup route (section 30.1).
/// </summary>
/// <remarks>
/// <para>
/// The security property is that an unclaimed instance exposes no API at all. Everything under
/// <c>/api</c> is refused with 503 except the setup endpoints themselves; the platform probes stay up
/// because a PaaS will otherwise kill the container before anyone can claim it, and the SPA's static
/// files are served because the setup route is a page in it.
/// </para>
/// <para>
/// The check short-circuits on a latch once a user exists, so the steady state costs one boolean read
/// per request. Setup mode ends permanently: nothing here can put the instance back into it.
/// </para>
/// </remarks>
public sealed class SetupModeMiddleware
{
    /// <summary>The API prefix the setup route lives under.</summary>
    public const string SetupApiPrefix = "/api/setup";

    private static readonly string[] AlwaysAllowed = ["/health", "/ready", "/api/instance", SetupApiPrefix];

    private readonly RequestDelegate next;
    private readonly SetupState state;

    public SetupModeMiddleware(RequestDelegate next, SetupState state)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(state);

        this.next = next;
        this.state = state;
    }

    public async Task InvokeAsync(HttpContext context, SetupModeService setup)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(setup);

        if (state.IsCompleted || !await setup.IsSetupRequiredAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        if (IsPermittedDuringSetup(context.Request.Path))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://charter.invalid/problems/setup-required",
            title = "Charter has not been set up yet",
            status = StatusCodes.Status503ServiceUnavailable,
            detail = "This instance has no users. Read the one-time setup token from the container logs "
                     + "and complete setup at /setup.",
        });
    }

    /// <summary>
    /// True for the handful of paths an unclaimed instance still answers: the probes, the AGPL
    /// instance descriptor, the setup endpoints, and the static files the setup page is built from.
    /// </summary>
    internal static bool IsPermittedDuringSetup(PathString path)
    {
        foreach (var allowed in AlwaysAllowed)
        {
            if (path.StartsWithSegments(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Everything else under /api is closed. Anything outside it is the SPA, which is only a
        // shell until it calls an endpoint - and every endpoint it could call is refused above.
        return !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }
}
