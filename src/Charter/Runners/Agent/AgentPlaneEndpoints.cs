using System.Globalization;
using System.Net.WebSockets;
using Charter.Api;
using Charter.Auth.Authorization;
using Charter.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Runners.Agent;

/// <summary>Body of <c>POST /api/agent/agents</c> — an admin generating a pairing token.</summary>
public sealed record CreateRunnerAgentBody
{
    /// <summary>What to call it before the agent reports its own <c>--name</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Minutes the pairing token stays live. Clamped; section 33.3 wants it short.</summary>
    public int? ExpiresInMinutes { get; init; }
}

/// <summary>Body of <c>POST /api/agent/agents/{id}/revoke</c>.</summary>
public sealed record RevokeRunnerAgentBody
{
    public string? Reason { get; init; }
}

/// <summary>
/// The two routes <c>charter-agent</c> talks to, plus the admin surface behind them (section 33.3).
/// </summary>
/// <remarks>
/// <para>
/// Both agent routes are anonymous to the cookie pipeline and authenticate on the agent's own bearer
/// credential instead. That is not a weaker check: the credential is a 256-bit secret held only as a
/// PBKDF2 verifier, it names exactly one agent row, and it grants nothing except the right to claim
/// work this instance already decided was claimable.
/// </para>
/// <para>
/// The connect route deliberately does <em>not</em> refuse an unknown <c>?protocol=</c> before the
/// upgrade. Section 33.6 asks for a mismatch to produce a clear message rather than a subtle failure,
/// and a refused upgrade reaches the operator as <c>WebSocketException: server returned 426</c>,
/// which names neither version. Accepting the socket and answering <c>hello</c> with
/// <c>protocol.mismatch</c> puts the daemon's own explanatory text in its log and leaves the agent
/// visible in the runners list with the reason, before closing with 4001.
/// </para>
/// </remarks>
public static class AgentPlaneEndpoints
{
    /// <summary>Bounds on the admin-chosen pairing token TTL.</summary>
    public const int MinimumPairingMinutes = 1;

    public const int MaximumPairingMinutes = 24 * 60;

    /// <summary>Where Settings → Runners reads and writes the registered agents.</summary>
    public const string AdminPath = "/api/agent/agents";

    /// <summary>
    /// Maps pairing, connect and the admin routes. Call after the authentication middleware, and
    /// after <c>UseWebSockets()</c> — without it an upgrade request never becomes a socket.
    /// </summary>
    public static IEndpointRouteBuilder MapCharterAgentPlane(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The two paths are the daemon's own constants, mirrored: AgentProtocol.PairPath and
        // AgentProtocol.ConnectPath. Building the routes from the constants rather than from string
        // literals is what keeps a rename on one side from silently orphaning the other.
        endpoints.MapPost(AgentProtocol.PairPath, PairAsync).AllowAnonymous();
        endpoints.MapGet(AgentProtocol.ConnectPath, ConnectAsync).AllowAnonymous();

        // Settings -> Runners. Written out rather than grouped: MapGroup with an empty pattern emits
        // "/api/agent/agents/", and a route table that reads differently from the URL the SPA calls is
        // the kind of thing that is only noticed once.
        endpoints.MapGet(AdminPath, ListAsync).RequireAuthorization();
        endpoints.MapPost(AdminPath, InviteAsync).RequireAuthorization();
        endpoints.MapPost($"{AdminPath}/{{agentId:guid}}/revoke", RevokeAsync).RequireAuthorization();

        return endpoints;
    }

    // ---------------------------------------------------------------------------------------------
    // Pairing (section 33.3, steps 1 and 2).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Trades a single-use pairing token for the long-lived agent credential.
    /// </summary>
    /// <remarks>
    /// The status codes are a contract <c>Charter.Agent.Pairing.HttpPairingClient</c> already
    /// implements and <c>AgentPlanePairingTests</c> pins: 401 and 403 are rejections the daemon does
    /// not retry, 404 and 410 mean expired or spent, 426 is a protocol refusal, and 408 or any 5xx is
    /// retryable. Getting one of these wrong is not a cosmetic bug — it is the difference between an
    /// operator being told to generate a fresh token and an agent retrying a dead one forever.
    /// </remarks>
    private static async Task<IResult> PairAsync(
        PairRequest? body,
        HttpContext context,
        AgentPlaneService agents,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                AgentErrorCodes.MalformedFrame,
                "The pairing request body was missing or unreadable.");
        }

        var result = await agents.PairAsync(body, cancellationToken);

        if (result.Ok)
        {
            context.Response.Headers[AgentProtocol.VersionHeader] =
                AgentProtocol.Version.ToString(CultureInfo.InvariantCulture);

            return Results.Json(result.Response!, AgentJson.Options);
        }

        var status = result.Outcome switch
        {
            AgentPairingOutcome.TokenRejected => StatusCodes.Status401Unauthorized,
            AgentPairingOutcome.Revoked => StatusCodes.Status403Forbidden,
            AgentPairingOutcome.TokenExpired => StatusCodes.Status410Gone,
            AgentPairingOutcome.ProtocolRefused => StatusCodes.Status426UpgradeRequired,
            _ => StatusCodes.Status400BadRequest,
        };

        return Failure(status, result.ErrorCode, result.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // The socket (sections 33.1, 33.6).
    // ---------------------------------------------------------------------------------------------

    private static async Task<IResult> ConnectAsync(
        HttpContext context,
        AgentPlaneService agents,
        AgentConnectionRegistry connections,
        AgentPlaneOptions options,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                AgentErrorCodes.MalformedFrame,
                $"{AgentProtocol.ConnectPath} is a WebSocket endpoint. charter-agent dials it with an "
                + "Upgrade request; a plain GET has nothing to do here.");
        }

        var authentication = await agents.AuthenticateAsync(Bearer(context), cancellationToken);

        if (authentication.Outcome == AgentAuthOutcome.Revoked)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                AgentErrorCodes.CredentialRevoked,
                "This agent has been revoked. Register a new one in Settings → Runners.");
        }

        if (!authentication.Ok)
        {
            return Failure(
                StatusCodes.Status401Unauthorized,
                AgentErrorCodes.CredentialRevoked,
                "The agent credential was not accepted. Re-pair this agent in Settings → Runners.");
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        await RunAsync(
            authentication.AgentId,
            new WebSocketAgentChannel(socket),
            connections,
            options,
            scopes,
            clock,
            loggers,
            cancellationToken);

        return Results.Empty;
    }

    /// <summary>
    /// Drives one connection from open to close. Public so a test can run the whole exchange over an
    /// in-memory channel rather than a listening port.
    /// </summary>
    public static async Task RunAsync(
        Guid agentId,
        IAgentChannel channel,
        AgentConnectionRegistry connections,
        AgentPlaneOptions options,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(loggers);

        var connection = new AgentConnection(
            agentId,
            channel,
            scopes,
            options,
            clock,
            loggers.CreateLogger<AgentConnection>());

        connections.Register(connection);

        try
        {
            await connection.RunAsync(cancellationToken);
        }
        finally
        {
            connections.Unregister(connection);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Admin (Settings -> Runners).
    // ---------------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ICharterAuthorizationService authorization,
        AgentPlaneService agents,
        CancellationToken cancellationToken)
    {
        var member = await CharterCaller.ResolveAsync(context.User, authorization, cancellationToken);
        if (member is null)
        {
            return ApiProblems.Unauthorized();
        }

        // Engineers read the runners list because section 27.3's queued-with-no-runner explanation
        // is addressed to them; only admins change it.
        if (!member.HasAnyRole(MemberRole.Admin, MemberRole.Engineer))
        {
            return ApiProblems.Forbidden("Only an engineer or an admin can see the runners list.");
        }

        return Results.Json(await agents.ListAsync(member.OrgId, cancellationToken), AgentJson.Options);
    }

    private static async Task<IResult> InviteAsync(
        CreateRunnerAgentBody? body,
        HttpContext context,
        ICharterAuthorizationService authorization,
        AgentPlaneService agents,
        CancellationToken cancellationToken)
    {
        var member = await CharterCaller.ResolveAsync(context.User, authorization, cancellationToken);
        if (member is null)
        {
            return ApiProblems.Unauthorized();
        }

        if (!member.HasRole(MemberRole.Admin))
        {
            return ApiProblems.Forbidden("Only an admin can register a runner.");
        }

        var name = string.IsNullOrWhiteSpace(body?.Name) ? "New agent" : body.Name.Trim();
        var minutes = Math.Clamp(
            body?.ExpiresInMinutes ?? (int)RunnerAgent.DefaultPairingTokenLifetime.TotalMinutes,
            MinimumPairingMinutes,
            MaximumPairingMinutes);

        var invitation = await agents.InviteAsync(
            member.OrgId,
            name,
            TimeSpan.FromMinutes(minutes),
            cancellationToken);

        // The one and only time the token is returned. It is not stored, so it cannot be shown again.
        return Results.Json(
            new
            {
                agentId = invitation.AgentId,
                pairingToken = invitation.PairingToken,
                expiresAt = invitation.ExpiresAt,
                command = $"charter-agent --server {BaseUrl(context)} --token {invitation.PairingToken}",
            },
            AgentJson.Options,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(
        Guid agentId,
        RevokeRunnerAgentBody? body,
        HttpContext context,
        ICharterAuthorizationService authorization,
        AgentPlaneService agents,
        CancellationToken cancellationToken)
    {
        var member = await CharterCaller.ResolveAsync(context.User, authorization, cancellationToken);
        if (member is null)
        {
            return ApiProblems.Unauthorized();
        }

        if (!member.HasRole(MemberRole.Admin))
        {
            return ApiProblems.Forbidden("Only an admin can revoke a runner.");
        }

        var reason = string.IsNullOrWhiteSpace(body?.Reason)
            ? "Revoked by an administrator."
            : body.Reason.Trim();

        return await agents.RevokeAsync(agentId, reason, cancellationToken)
            ? Results.Json(new { agentId, revoked = true, reason }, AgentJson.Options)
            : Results.NotFound();
    }

    // ---------------------------------------------------------------------------------------------

    private static IResult Failure(int status, string code, string message) =>
        Results.Json(
            new ErrorResponse { Error = code, Message = message },
            AgentJson.Options,
            statusCode: status);

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static string BaseUrl(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";
}
