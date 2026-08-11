using System.Text.Json;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Recaps;
using Charter.Refinement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>The payload of a <see cref="JobType.Recap"/> job.</summary>
/// <remarks>
/// A session, because the recap is about a run. The API's "Works" button writes a request and a spec
/// instead — it does not know which session was the one that worked — so both are read and either is
/// enough to find the run.
/// </remarks>
public sealed record RecapJobPayload
{
    /// <summary>The session to recap, when the caller knew it.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>The request, when the caller knew that instead.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>The specification, when the caller knew that instead.</summary>
    public Guid? SpecId { get; init; }

    /// <summary>Writes the payload in the spelling the orchestrator's own jobs use.</summary>
    public string ToJson() => $$"""{"session_id":"{{SessionId:D}}"}""";

    /// <summary>Parses a payload in either the API's camelCase or the orchestrator's snake_case.</summary>
    public static RecapJobPayload? TryParse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;

            var parsed = new RecapJobPayload
            {
                SessionId = Guid(root, "sessionId", "session_id"),
                RequestId = Guid(root, "requestId", "request_id"),
                SpecId = Guid(root, "specId", "spec_id"),
            };

            return parsed.SessionId is null && parsed.RequestId is null && parsed.SpecId is null
                ? null
                : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? Guid(JsonElement root, string camel, string snake)
    {
        foreach (var name in new[] { camel, snake })
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && System.Guid.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}

/// <summary>
/// Generates, stores, publishes and settles the engineer recap of section 14.
/// </summary>
/// <remarks>
/// <para>
/// <c>Charter.Recaps</c> builds the recap and <c>IRecapPublisher</c> posts it; neither had a caller,
/// so no session ever got one. This is the caller. It runs off the queue rather than off the
/// completion callback for the reason section 2.3 gives about everything else in this folder: the
/// control plane can restart between the run ending and the recap being written, and a queue row
/// survives that where an in-process continuation does not.
/// </para>
/// <para>
/// It is idempotent on the recap row. A session that already has one is not recapped again — a
/// second model pass over the same transcript would cost real money to produce a slightly different
/// answer, and section 14's recap is an orientation aid, not a thing worth two opinions of.
/// </para>
/// </remarks>
public sealed class RecapJobHandler : IQueuedJobHandler
{
    private readonly CharterDbContext _db;
    private readonly IRecapGenerator _generator;
    private readonly IRecapPublisher _publisher;
    private readonly ICredentialResolver _credentials;
    private readonly RecapOptions _recap;
    private readonly OrchestrationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecapJobHandler> _logger;

    public RecapJobHandler(
        CharterDbContext db,
        IRecapGenerator generator,
        IRecapPublisher publisher,
        ICredentialResolver credentials,
        RecapOptions recap,
        OrchestrationOptions options,
        TimeProvider clock,
        ILogger<RecapJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(recap);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _generator = generator;
        _publisher = publisher;
        _credentials = credentials;
        _recap = recap;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public JobType Type => JobType.Recap;

    /// <inheritdoc />
    public async Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (RecapJobPayload.TryParse(job.Payload) is not { } payload)
        {
            return JobHandlingResult.Failed(
                "The recap job's payload names neither a session, a request nor a specification.");
        }

        var sessionId = payload.SessionId ?? await ResolveSessionAsync(payload, cancellationToken);

        if (sessionId is not { } id)
        {
            return JobHandlingResult.Completed;
        }

        if (await _db.Recaps.AsNoTracking().AnyAsync(row => row.SessionId == id, cancellationToken))
        {
            return JobHandlingResult.Completed;
        }

        var context = await LoadAsync(id, cancellationToken);

        if (context is null)
        {
            return JobHandlingResult.Completed;
        }

        var (session, spec, request, repo) = context.Value;

        var events = await _db.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == id)
            .OrderBy(@event => @event.Seq)
            .Take(2_000)
            .ToListAsync(cancellationToken);

        var evidence = new RecapEvidence
        {
            SessionId = id,
            Spec = SpecDocumentMapper.ToDocument(spec),
            AutoDispatched = session.AutoDispatched,
            ApprovedBy = spec.ApprovedBy?.ToString("D"),
            Events = events,
            DenyPatterns = DenyPatterns(repo),
            AgentModel = session.AgentModel,
        };

        var credential = await _credentials.ResolveAsync(
            new ModelCredentialQuery(_recap.Model, request.RequesterId.ToString(), request.OrgId.ToString()),
            cancellationToken);

        if (credential.Credential is not { } resolved)
        {
            return JobHandlingResult.Deferred(
                "Every model credential is currently exhausted, so the recap is waiting for capacity.",
                _options.LockRetryInterval);
        }

        RecapResult result;

        try
        {
            result = await _generator.GenerateAsync(evidence, resolved.Credential, cancellationToken);
        }
        catch (ModelClientException exception)
        {
            await _credentials.ReportFailureAsync(resolved, exception, cancellationToken);
            return JobHandlingResult.Failed($"The recap model could not be reached: {exception.Message}");
        }
        catch (RecapException exception)
        {
            return JobHandlingResult.Failed($"The recap model returned something unusable: {exception.Message}");
        }

        var changeRequestNumber = await _db.ChangeRequests
            .AsNoTracking()
            .Where(row => row.SessionId == id)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => (int?)row.Number)
            .FirstOrDefaultAsync(cancellationToken);

        var publication = await _publisher.PublishAsync(result, repo, changeRequestNumber, cancellationToken);

        var now = _clock.GetUtcNow();

        // The body that is stored is the body that was published, fallback notice and all. A recap
        // that reads differently in Charter from the way it reads on the change request is a recap
        // two people quote at each other. `ToEntity` carries the structured payload across with it,
        // so the API reads section 14's sections as data instead of parsing headings back out.
        _db.Recaps.Add(result.ToEntity(publication.BodyMarkdown, now));

        Settle(request, id, result, now);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Recapped session {SessionId} onto {Surface} at {CostUsd} USD ({Removed} quality judgement(s) removed)",
            id,
            publication.Surface,
            result.Charge.CostUsd,
            result.VerdictStatementsRemoved);

        return JobHandlingResult.Completed;
    }

    /// <summary>
    /// Reserve then settle, in one transaction, because the work is already done (section 34.4).
    /// </summary>
    /// <remarks>
    /// A reservation exists to hold budget across a call whose cost is not yet known. There is no
    /// such window here — the recap has run and the charge is on the result — so the line is opened
    /// and closed together rather than leaving a reservation that a later sweep has to expire.
    /// Section 20b.5: a subscription-backed pass settles at zero dollars and still consumed quota,
    /// which is why both units are restated.
    /// </remarks>
    private void Settle(Request request, Guid sessionId, RecapResult result, DateTimeOffset now)
    {
        var subscription = result.Charge.Unit == ModelChargeUnit.SubscriptionQuota;

        var entry = subscription
            ? LedgerEntry.ReserveQuota(
                request.OrgId,
                request.RequesterId,
                LedgerCategory.Recap,
                quotaSessions: 1m,
                imputedUsd: result.Charge.NotionalCostUsd,
                sessionId: sessionId,
                now: now)
            : LedgerEntry.ReserveUsd(
                request.OrgId,
                request.RequesterId,
                LedgerCategory.Recap,
                result.Charge.CostUsd,
                sessionId: sessionId,
                now: now);

        entry.Settle(
            usd: subscription ? 0m : result.Charge.CostUsd,
            quotaSessions: subscription ? 1m : 0m,
            imputedUsd: subscription ? result.Charge.NotionalCostUsd : result.Charge.CostUsd,
            now);

        _db.LedgerEntries.Add(entry);
    }

    private async Task<Guid?> ResolveSessionAsync(RecapJobPayload payload, CancellationToken cancellationToken)
        => await (from session in _db.Sessions.AsNoTracking()
                  join spec in _db.Specs.AsNoTracking() on session.SpecId equals spec.Id
                  where (payload.SpecId != null && spec.Id == payload.SpecId)
                        || (payload.RequestId != null && spec.RequestId == payload.RequestId)
                  orderby session.CreatedAt descending
                  select (Guid?)session.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<(Session Session, Spec Spec, Request Request, Repo Repo)?> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var row = await (from session in _db.Sessions.AsNoTracking()
                         where session.Id == sessionId
                         join spec in _db.Specs.AsNoTracking() on session.SpecId equals spec.Id
                         join request in _db.Requests.AsNoTracking() on spec.RequestId equals request.Id
                         join repo in _db.Repos.AsNoTracking() on request.RepoId equals repo.Id
                         select new
                         {
                             Session = session,
                             Spec = spec,
                             Request = request,
                             Repo = repo,
                         })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : (row.Session, row.Spec, row.Request, row.Repo);
    }

    /// <summary>The repository's <c>scopes.deny</c> globs, so denylist-adjacent files float.</summary>
    private static IReadOnlyList<string> DenyPatterns(Repo repo)
        => RepoRefinementContext.For(repo).Scope.Deny;
}
