using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Charter.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Runners;

/// <summary>The body the workflow's credential-exchange step sends.</summary>
public sealed record CredentialExchangeRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("run_url")]
    public string? RunUrl { get; init; }

    /// <summary>
    /// The per-session half of the exchange, forwarded from the dispatch payload.
    /// </summary>
    /// <remarks>
    /// Required. The repository secret in the <c>Authorization</c> header proves the caller is a
    /// workflow run in the repository and nothing more — every run in that repository holds the same
    /// value — so it is this field that says <em>which</em> session is asking (sections 7.4, 16).
    /// </remarks>
    [JsonPropertyName("session_token")]
    public string? SessionToken { get; init; }
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
    /// Exchanges the repository's session secret, plus this session's dispatch token, for scoped
    /// short-TTL credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two factors, and both are load-bearing.</strong> The bearer secret is per repository —
    /// Charter writes one value into <c>secrets.CHARTER_SESSION_SECRET</c> and every workflow run in
    /// that repository reads the same one — so on its own it authenticates a repository, not a
    /// session. The dispatch token is minted per session and travels in the <c>client_payload</c> of
    /// the dispatch for that session alone. Without it, a run started for one session could name any
    /// other live session in the repository and be handed its contribute-scoped GitHub token and a
    /// twelve-hour event token, which is transcript and result forgery for work somebody else asked
    /// for (sections 7.4, 16).
    /// </para>
    /// <para>
    /// Neither factor substitutes for the other. The secret never appears in a payload — anyone with
    /// repository read access can see a <c>client_payload</c>, and the events API retains it — and the
    /// dispatch token grants nothing without the secret.
    /// </para>
    /// <para>
    /// The exchange is then gated on the session genuinely running (<see cref="SessionCredentialGuard"/>).
    /// A leaked repository secret therefore cannot mint an installation token whenever its holder
    /// likes, and not even for the session it was dispatched with once that session is over.
    /// </para>
    /// <para>
    /// The <c>run_url</c> the caller volunteers is checked against the session's own repository before
    /// it is written down (<see cref="RunnerRunReference"/>). It is the one field here the execution
    /// plane authors, the control plane later reads a <em>repository</em> back out of it, and a caller
    /// that lies about which repository it is running in is not one to hand a contribute-scoped token.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> ExchangeCredentialsAsync(
        Guid sessionId,
        CredentialExchangeRequest? body,
        HttpContext context,
        CharterDbContext db,
        SessionJournal journal,
        RunnerSessionTokens tokens,
        IRunnerCredentialBroker broker,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var decision = await SessionCredentialGuard.EvaluateAsync(db, journal, sessionId, cancellationToken);

        if (decision.Refusal is SessionCredentialRefusal.UnknownSession
            or SessionCredentialRefusal.Ended
            or SessionCredentialRefusal.Cancelled)
        {
            // Deliberately indistinguishable from a session that never existed: the caller has proved
            // nothing yet, and which sessions an instance is running is not something to leak.
            return Results.NotFound();
        }

        var repo = decision.RepoFullName!;

        if (!tokens.ValidateSessionSecret(repo, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        // The session-scoping factor. Checked after the repository secret so that a caller who cannot
        // authenticate at all learns nothing about which dispatch tokens are current.
        if (!tokens.ValidateDispatchToken(sessionId, body?.SessionToken))
        {
            return Results.Problem(
                "This request carries no valid session token. The credential exchange is scoped to one "
                + "session, so the workflow must forward `client_payload.session_token` as `session_token`. "
                + "A repository secret alone is not enough. Update `.github/workflows/agent-session.yml` "
                + "to the version Charter ships.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (decision.Refusal is SessionCredentialRefusal.NotDispatched)
        {
            return Results.Problem(decision.Explanation, statusCode: StatusCodes.Status409Conflict);
        }

        var reference = RunnerRunReference.Evaluate(body?.RunUrl, repo);

        if (reference.IsRejected)
        {
            RefuseReference(loggerFactory, sessionId, repo, body?.RunUrl, "credential exchange");

            // No token either. The exchange is the moment the instance decides how much of itself to
            // lend this run, and a run that has just misreported where it is running is not a run to
            // lend a contribute-scoped GitHub token and a twelve-hour event token to.
            return Results.Problem(reference.Refusal, statusCode: StatusCodes.Status403Forbidden);
        }

        if (reference.IsRecordable)
        {
            await journal.AppendAsync(
                sessionId,
                EventTypes.SessionStarted,
                new JsonObject { ["run_url"] = body!.RunUrl }.ToJsonString(),
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
    /// <para>
    /// It is also where an oversized payload stops being a Postgres row. An adapter's <c>raw</c>
    /// carries the agent's whole JSONL line - a <c>Write</c> tool call contains the file it wrote -
    /// and <c>events</c> is already the largest table in the schema (section 5). When an object store
    /// is configured, <see cref="TranscriptOffload"/> moves the oversized strings into it and leaves
    /// their tail plus a <c>file_ref</c> behind; when none is, the payload is stored exactly as it
    /// arrives, which is what every instance did before storage existed (section 2.3).
    /// </para>
    /// <para>
    /// <c>session_started</c> is the other exception, and it is a security one. Its <c>run_url</c> is
    /// the only thing a runner posts here that the control plane later <em>addresses something with</em>
    /// — <see cref="Charter.Orchestration.SessionJournal.SummarizeAsync"/> folds it into the session's
    /// external reference and cancellation reads a repository back out of it. The event token proves
    /// which session is posting, never what is true, so the reference is checked against the session's
    /// own repository before it is allowed into the journal (<see cref="RunnerRunReference"/>).
    /// </para>
    /// </remarks>
    internal static async Task<IResult> IngestEventAsync(
        Guid sessionId,
        RunnerEventRequest body,
        HttpContext context,
        CharterDbContext db,
        SessionJournal journal,
        SessionMilestones milestones,
        RunnerSessionTokens tokens,
        NeedsInputAnnouncer needsInput,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!tokens.ValidateEventToken(sessionId, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(body.Type))
        {
            return Results.BadRequest(new { error = "An event needs a type." });
        }

        // Before the payload is rewritten by offload, and before anything is appended: a rejected
        // reference must leave no trace on the session at all.
        if (string.Equals(body.Type, EventTypes.SessionStarted, StringComparison.Ordinal)
            && ReadRunUrl(body.Payload) is { } claimed)
        {
            var repo = await SessionCredentialGuard.SessionRepoFullNameAsync(db, sessionId, cancellationToken);
            var reference = RunnerRunReference.Evaluate(claimed, repo);

            if (reference.IsRejected)
            {
                RefuseReference(loggerFactory, sessionId, repo, claimed, "session_started event");

                return Results.Problem(reference.Refusal, statusCode: StatusCodes.Status403Forbidden);
            }
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

        // Derived from what arrived, before anything is rewritten: an event that is offloaded on one
        // delivery and inlined on the next - because storage went away in between - must still be the
        // same event, or a retry would double the transcript.
        if (context.RequestServices.GetService<TranscriptOffload>() is { Enabled: true } offload)
        {
            payload = await offload.RewriteAsync(sessionId, body.Type, payload, key, cancellationToken);
        }

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

    /// <summary>
    /// Records the runner's terminal report and settles a run that ended badly.
    /// </summary>
    /// <remarks>
    /// The <c>run_url</c> here is checked the same way as everywhere else, but a failure is handled
    /// differently: the report itself is still accepted. Refusing it would leave the session running in
    /// Charter's eyes until recovery timed it out, which is a worse outcome than a missing link, so the
    /// unattributable reference is dropped and the result stands. Nothing reads this field to address
    /// anything today; it is validated so that nothing can start to.
    /// </remarks>
    private static async Task<IResult> ReportResultAsync(
        Guid sessionId,
        RunnerResultRequest body,
        HttpContext context,
        CharterDbContext db,
        SessionJournal journal,
        SessionCoordinator coordinator,
        RunnerSessionTokens tokens,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!tokens.ValidateEventToken(sessionId, Bearer(context)))
        {
            return Results.Unauthorized();
        }

        var state = string.IsNullOrWhiteSpace(body.State) ? "failed" : body.State.Trim().ToLowerInvariant();

        var repo = await SessionCredentialGuard.SessionRepoFullNameAsync(db, sessionId, cancellationToken);
        var reference = RunnerRunReference.Evaluate(body.RunUrl, repo);

        if (reference.IsRejected)
        {
            RefuseReference(loggerFactory, sessionId, repo, body.RunUrl, "result callback");
        }

        await journal.AppendAsync(
            sessionId,
            EventTypes.SessionEnded,
            new JsonObject
            {
                ["state"] = state,
                ["run_url"] = reference.IsRecordable ? body.RunUrl : null,
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

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>
    /// The <c>run_url</c> of a <c>session_started</c> payload, read exactly as the journal reads it.
    /// </summary>
    /// <remarks>
    /// Only a JSON string counts, because only a JSON string becomes an external reference in
    /// <c>SessionJournal.SummarizeAsync</c>. Reading a wider set here would refuse callbacks that could
    /// never have poisoned anything; reading a narrower one would let something through.
    /// </remarks>
    private static string? ReadRunUrl(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty("run_url", out var value)
            || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Section 19: an operator has to be able to see this happen.
    /// </summary>
    /// <remarks>
    /// A warning rather than information, because the only ways to reach it are a target repository
    /// running a workflow Charter did not ship, a repository renamed on GitHub without being
    /// reconnected here, and a session trying to name somebody else's repository. All three want a
    /// human. The <c>Authorization</c> header is not touched — the reference is not a credential and no
    /// credential is logged (section 19).
    /// </remarks>
    private static void RefuseReference(
        ILoggerFactory loggerFactory,
        Guid sessionId,
        string? repoFullName,
        string? runUrl,
        string callback)
        => loggerFactory.CreateLogger(typeof(RunnerCallbackEndpoints)).LogWarning(
            "Refused the run reference a runner reported on the {Callback} for session {SessionId}: it "
            + "does not name a workflow run in {Repo}. Reported: {Reference}",
            callback,
            sessionId,
            repoFullName ?? "(unknown)",
            RunnerRunReference.Describe(runUrl));

    /// <summary>A stable identity for an event that carries no counter.</summary>
    internal static string ContentKey(string type, string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}\0{payload}"));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
