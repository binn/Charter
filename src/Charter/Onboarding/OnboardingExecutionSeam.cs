using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Data;
using Charter.Domain;

namespace Charter.Onboarding;

/// <summary>
/// The payload a recon or smoke-test job carries into the execution plane.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam. Onboarding decides <em>that</em> a read-only recon run should happen and
/// persists the fact; the execution plane decides where it runs and on what. The two meet on this
/// payload and on the callbacks in <see cref="IOnboardingRunCallbacks"/>, and nowhere else — nothing
/// in this namespace resolves a runner, a session or a job claim.
/// </para>
/// <para>
/// The GitHub credential is deliberately absent from the payload. Section 7.4 gives the runner a
/// short-TTL, single-repository installation token, minted at claim time through
/// <c>IGitHubRunnerCredentialFactory</c>; writing one into a queued job row would put a live
/// credential in the database for as long as the queue is backed up.
/// </para>
/// </remarks>
public sealed record OnboardingJobPayload
{
    /// <summary>The repository row this run is for.</summary>
    [JsonPropertyName("repo_id")]
    public required Guid RepoId { get; init; }

    /// <summary>The organisation, for budget and audit attribution.</summary>
    [JsonPropertyName("org_id")]
    public required Guid OrgId { get; init; }

    /// <summary><c>owner/name</c>.</summary>
    [JsonPropertyName("repo_full_name")]
    public required string RepoFullName { get; init; }

    /// <summary>The GitHub App installation to mint a token through.</summary>
    [JsonPropertyName("installation_id")]
    public required long InstallationId { get; init; }

    /// <summary>The branch to read, and to branch from.</summary>
    [JsonPropertyName("base_branch")]
    public required string BaseBranch { get; init; }

    /// <summary>
    /// The human who asked. Section 7.3, guardrail 5: every agent action is attributable to a named
    /// human, and onboarding is no exception.
    /// </summary>
    [JsonPropertyName("requested_by_user_id")]
    public Guid? RequestedByUserId { get; init; }

    /// <summary>
    /// Whether the run may only read. True for recon (section 9, step 2), which is read-only by
    /// definition, and false for the smoke test, which has to open a pull request.
    /// </summary>
    [JsonPropertyName("read_only")]
    public required bool ReadOnly { get; init; }

    /// <summary>Serialises the payload for the job row.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, OnboardingJson.Options);

    /// <summary>Reads a payload back off a job row.</summary>
    public static OnboardingJobPayload? FromJson(string json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<OnboardingJobPayload>(json, OnboardingJson.Options);
}

/// <summary>The JSON spelling of the onboarding seam. Snake case, matching the rest of the queue.</summary>
internal static class OnboardingJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Hands an onboarding run to the execution plane.
/// </summary>
/// <remarks>
/// Onboarding depends on this interface and never on a runner. The default implementation enqueues a
/// <see cref="JobType.Recon"/> or <see cref="JobType.SmokeTest"/> job, which is the handshake the
/// execution plane already claims from: whatever routes and runs those jobs is free to change without
/// section 9 knowing.
/// </remarks>
public interface IOnboardingRunDispatcher
{
    /// <summary>Queues the read-only recon run (section 9, step 2).</summary>
    Task<Guid> DispatchReconAsync(OnboardingJobPayload payload, CancellationToken cancellationToken = default);

    /// <summary>Queues the canned smoke test (section 9, step 4).</summary>
    Task<Guid> DispatchSmokeTestAsync(OnboardingJobPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// The other half of the seam: what the execution plane calls when a run finishes.
/// </summary>
/// <remarks>
/// Implemented by <see cref="OnboardingService"/>. Declared as its own interface so the worker that
/// claims a recon job depends on four methods rather than on the whole onboarding service.
/// </remarks>
public interface IOnboardingRunCallbacks
{
    /// <summary>Recon finished. Advances to <c>configuring</c> and proposes the scope config.</summary>
    Task<OnboardingOutcome> CompleteReconAsync(
        Guid repoId,
        ReconReport report,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Recon failed. Returns the repository to <c>pending</c>.</summary>
    Task<OnboardingOutcome> FailReconAsync(
        Guid repoId,
        string reason,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The smoke test finished, passing or not.</summary>
    Task<OnboardingOutcome> CompleteSmokeTestAsync(
        Guid repoId,
        SmokeTestReport report,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Enqueues onboarding runs on the Postgres job queue (section 2.3).</summary>
public sealed class JobQueueOnboardingDispatcher : IOnboardingRunDispatcher
{
    private readonly JobQueue _queue;

    public JobQueueOnboardingDispatcher(JobQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    /// <inheritdoc />
    public async Task<Guid> DispatchReconAsync(
        OnboardingJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Priority above an ordinary build: an engineer is sitting in the onboarding wizard waiting
        // for this, and a queue full of feature work would otherwise make connecting a repo feel
        // broken.
        var job = await _queue.EnqueueAsync(
            JobType.Recon,
            (payload with { ReadOnly = true }).ToJson(),
            priority: 10,
            cancellationToken: cancellationToken);

        return job.Id;
    }

    /// <inheritdoc />
    public async Task<Guid> DispatchSmokeTestAsync(
        OnboardingJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var job = await _queue.EnqueueAsync(
            JobType.SmokeTest,
            (payload with { ReadOnly = false }).ToJson(),
            priority: 10,
            cancellationToken: cancellationToken);

        return job.Id;
    }
}
