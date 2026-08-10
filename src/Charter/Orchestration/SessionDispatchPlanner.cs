using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Microsoft.EntityFrameworkCore;

namespace Charter.Orchestration;

/// <summary>
/// The payload of a <see cref="JobType.Build"/> job.
/// </summary>
/// <remarks>
/// <para>
/// Section 5 gives <see cref="Job"/> no foreign keys on purpose: the queue must stay claimable
/// without touching another table, and the payload carries whichever identifiers the handler needs.
/// This is that payload.
/// </para>
/// <para>
/// Everything except <see cref="SessionId"/> is optional, and anything omitted is resolved from the
/// database at dispatch time. That matters after a restart: the payload written by a control plane
/// three versions ago must still dispatch, and the way to guarantee that is to keep the required part
/// of it to one identifier.
/// </para>
/// </remarks>
public sealed record BuildJobPayload
{
    [JsonPropertyName("session_id")]
    public required Guid SessionId { get; init; }

    [JsonPropertyName("repo")]
    public string? RepoFullName { get; init; }

    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("base_commit_sha")]
    public string? BaseCommitSha { get; init; }

    [JsonPropertyName("adapter")]
    public string? AdapterId { get; init; }

    [JsonPropertyName("runner_image")]
    public string? RunnerImage { get; init; }

    [JsonPropertyName("allow_paths")]
    public IReadOnlyList<string>? AllowPaths { get; init; }

    [JsonPropertyName("deny_paths")]
    public IReadOnlyList<string>? DenyPaths { get; init; }

    [JsonPropertyName("timeout_minutes")]
    public int? TimeoutMinutes { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a payload, tolerating anything a newer or older Charter added.</summary>
    public static BuildJobPayload? TryParse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return JsonSerializer.Deserialize<BuildJobPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Turns a queued job into everything a backend needs, reading only from Postgres.</summary>
public interface ISessionDispatchPlanner
{
    /// <summary>
    /// Builds the dispatch, or returns null when the session no longer wants one — cancelled,
    /// terminal, or already handed to a backend.
    /// </summary>
    Task<RunnerDispatch?> PlanAsync(BuildJobPayload payload, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SessionDispatchPlanner : ISessionDispatchPlanner
{
    private readonly CharterDbContext _db;
    private readonly OrchestrationOptions _options;

    public SessionDispatchPlanner(CharterDbContext db, OrchestrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<RunnerDispatch?> PlanAsync(
        BuildJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var session = await _db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == payload.SessionId, cancellationToken);

        if (session is null || session.IsTerminal || session.CancelRequestedAt is not null)
        {
            return null;
        }

        var repo = await ResolveRepoAsync(session, cancellationToken);
        var scope = ReadScope(repo?.CharterConfigSnapshot, payload);

        var repoFullName = payload.RepoFullName ?? repo?.FullName;
        if (string.IsNullOrWhiteSpace(repoFullName))
        {
            throw new InvalidOperationException(
                $"Session {session.Id} cannot be dispatched: neither the job payload nor the repository "
                + "row names a GitHub repository.");
        }

        return new RunnerDispatch(
            session.Id,
            repoFullName,
            payload.BaseBranch ?? repo?.BaseBranch ?? "main",
            payload.BaseCommitSha ?? session.BaseCommitSha ?? repo?.BaseBranch ?? "main",
            payload.AdapterId ?? "claude-code",
            session.AgentModel,
            payload.RunnerImage ?? ReadString(repo?.CharterConfigSnapshot, "runner_image"),
            _options.CallbackUrlFor(session.Id),
            _options.SpecUrlFor(session.Id),
            scope,
            [],
            payload.TimeoutMinutes ?? _options.DefaultTimeoutMinutes,
            DispatchKeyFor(session.Id));
    }

    /// <summary>
    /// The idempotency key for a session's dispatch.
    /// </summary>
    /// <remarks>
    /// A pure function of the session id, which is the point: the dispatch event derived from it has
    /// the same primary key whichever process writes it, so a second dispatch after a restart is
    /// refused by Postgres rather than by a flag somebody remembered to check (section 2.3).
    /// </remarks>
    public static string DispatchKeyFor(Guid sessionId) => $"dispatch:{sessionId:D}";

    private async Task<Repo?> ResolveRepoAsync(Session session, CancellationToken cancellationToken)
        => await (from spec in _db.Specs.AsNoTracking()
                  where spec.Id == session.SpecId
                  join request in _db.Requests.AsNoTracking() on spec.RequestId equals request.Id
                  join repo in _db.Repos.AsNoTracking() on request.RepoId equals repo.Id
                  select repo)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Reads <c>scopes.allow</c> and <c>scopes.deny</c> from the repository's config snapshot.
    /// </summary>
    /// <remarks>
    /// The job payload can only <em>tighten</em> what the repository declares — its lists are appended
    /// to the deny side and intersected on the allow side is not needed here, because the shim applies
    /// deny before allow and an empty allow list means the whole workspace. Section 7.5's composition
    /// rule in the simplest possible form: a repository's deny entries always survive.
    /// </remarks>
    internal static RunnerPathScope ReadScope(string? configSnapshotJson, BuildJobPayload payload)
    {
        var allow = new List<string>(payload.AllowPaths ?? []);
        var deny = new List<string>(payload.DenyPaths ?? []);

        if (!string.IsNullOrWhiteSpace(configSnapshotJson))
        {
            try
            {
                using var document = JsonDocument.Parse(configSnapshotJson);

                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("scopes", out var scopes)
                    && scopes.ValueKind == JsonValueKind.Object)
                {
                    allow.AddRange(ReadArray(scopes, "allow"));
                    deny.AddRange(ReadArray(scopes, "deny"));
                }
            }
            catch (JsonException)
            {
                // An unreadable snapshot must not widen anything. Deny everything outside the payload's
                // own allow list and let the session fail loudly on its first write instead.
                deny.Add("**");
            }
        }

        return new RunnerPathScope(
            [.. allow.Distinct(StringComparer.Ordinal)],
            [.. deny.Distinct(StringComparer.Ordinal)]);
    }

    private static IEnumerable<string> ReadArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    private static string? ReadString(string? configSnapshotJson, string name)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(configSnapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
