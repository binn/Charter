using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Charter.Runners;

/// <summary>The body the workflow's credential-exchange step sends.</summary>
public sealed record CredentialExchangeRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("run_url")]
    public string? RunUrl { get; init; }
}

/// <summary>What it gets back. Field names are read by <c>jq</c> in the shipped workflow.</summary>
public sealed record CredentialExchangeResponse
{
    [JsonPropertyName("github_token")]
    public required string GitHubToken { get; init; }

    [JsonPropertyName("model_api_key")]
    public string? ModelApiKey { get; init; }

    [JsonPropertyName("event_token")]
    public required string EventToken { get; init; }
}

/// <summary>One streamed event from a sandbox.</summary>
public sealed record RunnerEventRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    /// <summary>The shim's own counter, when it has one. Absent from the workflow's curl calls.</summary>
    [JsonPropertyName("index")]
    public long? Index { get; init; }
}

/// <summary>The terminal report the workflow always sends, even on failure.</summary>
public sealed record RunnerResultRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("run_url")]
    public string? RunUrl { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// The callbacks a sandbox makes: exchange credentials, stream events, report a result, fetch the spec.
/// </summary>
/// <remarks>
/// <para>
/// The contract is fixed by <c>.github/workflows/agent-session.yml</c>, which posts to
/// <c>{callback_url}/credentials</c>, <c>{callback_url}/events</c> and <c>{callback_url}/result</c>.
/// Charter builds <c>callback_url</c> as <c>{CHARTER_BASE_URL}/api/runners/sessions/{id}</c>, so these
/// routes hang off exactly that prefix.
/// </para>
/// <para>
/// Every write goes through <see cref="SessionJournal"/> with an idempotency key, because a runner
/// that loses its connection retries, and a control plane that restarted has no way to know whether
/// it saw a delivery before. Duplicate suppression therefore has to be a property of the storage, not
/// of anybody's memory (section 2.3).
/// </para>
/// </remarks>
public static class RunnerCallbackEndpoints
{
    /// <summary>The prefix <c>callback_url</c> is built from.</summary>
    public const string RoutePrefix = "/api/runners/sessions/{sessionId:guid}";

    /// <summary>Maps the sandbox callbacks. Call after authentication middleware.</summary>
    public static IEndpointRouteBuilder MapCharterRunnerCallbacks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.MapPost("/credentials", ExchangeCredentialsAsync);
        group.MapPost("/events", IngestEventAsync);
        group.MapPost("/result", ReportResultAsync);
        group.MapGet("/spec", FetchSpecAsync);

        return endpoints;
    }

    /// <summary>
    /// Exchanges the repository's session secret for scoped, short-TTL credentials.
    /// </summary>
    /// <remarks>
    /// The exchange is gated on the session actually being dispatched and not terminal. A leaked
    /// repository secret therefore cannot mint an installation token whenever its holder likes — only
    /// while a session Charter itself started is genuinely in flight.
    /// </remarks>
    private static async Task<IResult> ExchangeCredentialsAsync(
        Guid sessionId,
        CredentialExchangeRequest? body,
        HttpContext context,
        CharterDbContext db,
        SessionJournal journal,
        RunnerSessionTokens tokens,
        IRunnerCredentialBroker broker,
        CancellationToken cancellationToken)
    {
        var session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return Results.NotFound();
        }

        var repo = await RepoFullNameAsync(db, session.SpecId, cancellationToken);
        if (repo is null)
        {
            return Results.NotFound();
        }

        if (!tokens.ValidateSessionSecret(repo, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        var summary = await journal.SummarizeAsync(sessionId, cancellationToken);
        if (!summary.Dispatched)
        {
            return Results.Problem(
                "This session has not been dispatched, so no credentials will be issued for it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!string.IsNullOrWhiteSpace(body?.RunUrl))
        {
            await journal.AppendAsync(
                sessionId,
                EventTypes.SessionStarted,
                new JsonObject { ["run_url"] = body.RunUrl }.ToJsonString(),
                $"run-url:{body.RunUrl}",
                cancellationToken: cancellationToken);
        }

        var credentials = await broker.IssueAsync(sessionId, repo, cancellationToken);

        return Results.Json(new CredentialExchangeResponse
        {
            GitHubToken = credentials.GitHubToken,
            ModelApiKey = credentials.ModelApiKey,
            EventToken = tokens.IssueEventToken(sessionId),
        });
    }

    /// <summary>
    /// Records one streamed event, and acts on the one kind that needs a human.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost every event is a line of transcript and nothing more. <c>question</c> is the exception:
    /// section 6 makes <c>NeedsInput</c> one of exactly two states that reach a person, and an agent
    /// that stopped to ask has blocked the whole request on somebody who does not know yet. The
    /// announcement is deliberately downstream of the append, so the journal's idempotency key — not a
    /// second rule here — is what makes a redelivered question notify nobody twice.
    /// </para>
    /// <para>
    /// Milestone promotion runs here too, and for the same reason it sits after the append: this is
    /// the one place every backend's events pass through, and section 11 will not accept a build that
    /// streams nothing to pane 1 for twenty minutes. A promotion that finds the session already past
    /// that label does nothing, so a replayed stream is still one thread.
    /// </para>
    /// </remarks>
    private static async Task<IResult> IngestEventAsync(
        Guid sessionId,
        RunnerEventRequest body,
        HttpContext context,
        SessionJournal journal,
        SessionMilestones milestones,
        RunnerSessionTokens tokens,
        NeedsInputAnnouncer needsInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!tokens.ValidateEventToken(sessionId, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(body.Type))
        {
            return Results.BadRequest(new { error = "An event needs a type." });
        }

        var payload = body.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : body.Payload.GetRawText();

        // With an index the runner names its own event; without one - the shipped workflow's curl
        // calls have no counter - the content is the identity. Either way a replayed delivery is a
        // no-op rather than a second copy of the line (section 2.3).
        var key = body.Index is { } index
            ? $"runner:{index}"
            : $"runner-content:{ContentKey(body.Type, payload)}";

        var appended = await journal.AppendAsync(
            sessionId,
            body.Type,
            payload,
            key,
            cancellationToken: cancellationToken);

        var asked = false;

        if (appended.Appended
            && string.Equals(body.Type, RunnerEventTypes.Question, StringComparison.Ordinal)
            && RunnerEventTypes.ReadQuestion(payload) is { } question)
        {
            var announcement = await needsInput.AskAsync(sessionId, question, cancellationToken);
            asked = announcement.Moved;
        }

        var milestone = appended.Appended
            ? await milestones.PromoteAsync(sessionId, appended.EventId, body.Type, cancellationToken)
            : null;

        return Results.Json(new
        {
            seq = appended.Seq,
            appended = appended.Appended,
            needsInput = asked,
            milestone = milestone?.ToString(),
        });
    }

    private static async Task<IResult> ReportResultAsync(
        Guid sessionId,
        RunnerResultRequest body,
        HttpContext context,
        SessionJournal journal,
        SessionCoordinator coordinator,
        RunnerSessionTokens tokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!tokens.ValidateEventToken(sessionId, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        var state = string.IsNullOrWhiteSpace(body.State) ? "failed" : body.State.Trim().ToLowerInvariant();

        await journal.AppendAsync(
            sessionId,
            EventTypes.SessionEnded,
            new JsonObject
            {
                ["state"] = state,
                ["run_url"] = body.RunUrl,
                ["message"] = body.Message,
            }.ToJsonString(),
            $"result:{sessionId:D}:{state}",
            cancellationToken: cancellationToken);

        // Section 6 puts PROpen after Running, and opening the pull request is phase 3's work, so a
        // completed run is not settled here - only a run that ended badly is.
        if (SessionRecovery.MapTerminal(state) is { } status)
        {
            await coordinator.SettleAsync(sessionId, status, body.Message, cancellationToken);
        }

        return Results.Json(new { state });
    }

    /// <summary>
    /// Hands the sandbox the approved spec.
    /// </summary>
    /// <remarks>
    /// Section 16: the agent never sees raw requester text. What it gets is the refined, human-approved
    /// spec, which is model-authored — refinement is the sanitisation boundary and approval is the
    /// human review of what the agent will be told.
    /// </remarks>
    private static async Task<IResult> FetchSpecAsync(
        Guid sessionId,
        HttpContext context,
        CharterDbContext db,
        RunnerSessionTokens tokens,
        CancellationToken cancellationToken)
    {
        if (!tokens.ValidateEventToken(sessionId, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        var spec = await (from session in db.Sessions.AsNoTracking()
                          where session.Id == sessionId
                          join candidate in db.Specs.AsNoTracking() on session.SpecId equals candidate.Id
                          select new { candidate.Title, candidate.BodyMd, candidate.AcceptanceCriteria })
            .FirstOrDefaultAsync(cancellationToken);

        if (spec is null)
        {
            return Results.NotFound();
        }

        return Results.Text($"# {spec.Title}\n\n{spec.BodyMd}\n", "text/markdown");
    }

    private static async Task<string?> RepoFullNameAsync(
        CharterDbContext db,
        Guid specId,
        CancellationToken cancellationToken)
        => await (from spec in db.Specs.AsNoTracking()
                  where spec.Id == specId
                  join request in db.Requests.AsNoTracking() on spec.RequestId equals request.Id
                  join repo in db.Repos.AsNoTracking() on request.RepoId equals repo.Id
                  select repo.FullName)
            .FirstOrDefaultAsync(cancellationToken);

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>A stable identity for an event that carries no counter.</summary>
    internal static string ContentKey(string type, string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}\0{payload}"));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
