using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Charter.Api.Contracts;

/// <summary>
/// One frame of the live stream the SPA folds into the detail it already holds
/// (<c>ClientApp/src/features/request/applyStreamEvent.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// Section 2.3: the container can restart mid-session and there is no in-memory orchestration state,
/// so a client reconnects, refetches, and then replays whatever the hub sends it. Every frame must
/// therefore be safe to apply twice.
/// </para>
/// <para>
/// <see cref="Id"/> is what makes that checkable. It is derived from the frame's <em>content</em>,
/// not from a counter or a clock, by <see cref="RequestStreamEventId.Compute"/>: the same milestone
/// republished unchanged produces the same id, and a milestone whose label changed produces a
/// different one. An id is therefore never reused for different content, which is exactly the
/// property the client's dedupe-by-id needs in order to be correct rather than merely lucky.
/// </para>
/// <para>
/// <see cref="Type"/> is a plain property rather than a polymorphic discriminator because SignalR
/// hands arguments to the serialiser as <see cref="object"/>: the runtime type is what gets written,
/// and a discriminator configured on the base type would be skipped.
/// </para>
/// </remarks>
public abstract record RequestStreamEvent
{
    private protected RequestStreamEvent(string type, string id)
    {
        Type = type;
        Id = id;
    }

    /// <summary>The union tag the client switches on.</summary>
    public string Type { get; }

    /// <summary>Content-derived and stable. See the remarks.</summary>
    public string Id { get; }
}

/// <summary>
/// The factories for every frame in the <c>RequestStreamEvent</c> union.
/// </summary>
/// <remarks>
/// They live beside the records rather than on them because each frame's payload property shares a
/// name with the frame it belongs to, which is what the SPA's union spells and is therefore not
/// negotiable.
/// </remarks>
public static class RequestStreamEvents
{
    /// <summary>A milestone appeared in the status thread (section 11).</summary>
    public static MilestoneStreamEvent Milestone(MilestoneResponse milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        return new MilestoneStreamEvent(RequestStreamEventId.Compute("milestone", milestone.Id, milestone), milestone);
    }

    /// <summary>An existing milestone changed state or gained a detail.</summary>
    public static MilestoneUpdatedStreamEvent MilestoneUpdated(MilestoneResponse milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        return new MilestoneUpdatedStreamEvent(
            RequestStreamEventId.Compute("milestone_updated", milestone.Id, milestone),
            milestone);
    }

    /// <summary>The request moved along the section 6 state machine.</summary>
    public static StatusStreamEvent Status(ApiRequestStatus status, string? awaitingApprovalFrom = null)
        => new(
            RequestStreamEventId.Compute("status", status.ToString(), new { status, awaitingApprovalFrom }),
            status,
            awaitingApprovalFrom);

    /// <summary>A turn was added to the refinement conversation.</summary>
    public static RefinementMessageStreamEvent RefinementMessage(RefinementMessageResponse message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new RefinementMessageStreamEvent(
            RequestStreamEventId.Compute("refinement_message", message.Id, message),
            message);
    }

    /// <summary>The typing indicator. Carries no time estimate (section 6).</summary>
    public static CharterThinkingStreamEvent CharterThinking(bool thinking)
        => new(
            RequestStreamEventId.Compute("charter_thinking", thinking ? "on" : "off", thinking),
            thinking);

    /// <summary>Charter proposed a Spec version (section 10b).</summary>
    public static SpecProposedStreamEvent SpecProposed(RequesterSpecResponse spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new SpecProposedStreamEvent(
            RequestStreamEventId.Compute("spec_proposed", $"{spec.Id}:{spec.Version}", spec),
            spec);
    }

    /// <summary>A verification artifact appeared or was replaced (section 27.1).</summary>
    public static ArtifactStreamEvent Artifact(VerificationArtifactResponse artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new ArtifactStreamEvent(
            RequestStreamEventId.Compute("artifact", artifact.Id, artifact),
            artifact);
    }

    /// <summary>An artifact changed card state (section 27.7).</summary>
    public static ArtifactStateStreamEvent ArtifactState(
        string artifactId,
        ApiArtifactState state,
        DateTimeOffset? expiresAt = null)
        => new(
            RequestStreamEventId.Compute("artifact_state", artifactId, new { artifactId, state, expiresAt }),
            artifactId,
            state,
            expiresAt);

    /// <summary>Section 11: failure has dignity. Plain language, never a stack trace.</summary>
    public static FailedStreamEvent Failed(string failureSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureSummary);
        return new FailedStreamEvent(
            RequestStreamEventId.Compute("failed", "failed", failureSummary),
            failureSummary);
    }

    /// <summary>The session stopped. The client clears <c>live</c> and <c>cancellable</c>.</summary>
    public static EndedStreamEvent Ended(DateTimeOffset endedAt)
        => new(RequestStreamEventId.Compute("ended", "ended", endedAt), endedAt);
}

/// <summary>Computes the content-derived identity described on <see cref="RequestStreamEvent"/>.</summary>
public static class RequestStreamEventId
{
    /// <summary>
    /// <c>{type}:{key}:{fingerprint}</c>, where the fingerprint is a truncated SHA-256 of the
    /// canonical JSON of <paramref name="content"/>.
    /// </summary>
    /// <remarks>
    /// Hashing the serialised content — rather than stamping a sequence number or a timestamp — is
    /// what makes republishing idempotent across a container restart. Two publishes of identical
    /// content collide by design; two publishes of different content cannot.
    /// </remarks>
    public static string Compute(string type, string key, object content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);

        var canonical = JsonSerializer.Serialize(content, content.GetType(), CharterApiJson.Options);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return string.Concat(type, ":", key, ":", Convert.ToHexStringLower(digest.AsSpan(0, 8)));
    }
}

/// <summary><c>{ type: 'milestone', milestone }</c>.</summary>
public sealed record MilestoneStreamEvent : RequestStreamEvent
{
    internal MilestoneStreamEvent(string id, MilestoneResponse milestone)
        : base("milestone", id) => Milestone = milestone;

    public MilestoneResponse Milestone { get; }
}

/// <summary><c>{ type: 'milestone_updated', milestone }</c>.</summary>
public sealed record MilestoneUpdatedStreamEvent : RequestStreamEvent
{
    internal MilestoneUpdatedStreamEvent(string id, MilestoneResponse milestone)
        : base("milestone_updated", id) => Milestone = milestone;

    public MilestoneResponse Milestone { get; }
}

/// <summary><c>{ type: 'status', status, awaitingApprovalFrom? }</c>.</summary>
public sealed record StatusStreamEvent : RequestStreamEvent
{
    internal StatusStreamEvent(string id, ApiRequestStatus status, string? awaitingApprovalFrom)
        : base("status", id)
    {
        Status = status;
        AwaitingApprovalFrom = awaitingApprovalFrom;
    }

    public ApiRequestStatus Status { get; }

    public string? AwaitingApprovalFrom { get; }
}

/// <summary><c>{ type: 'refinement_message', message }</c>.</summary>
public sealed record RefinementMessageStreamEvent : RequestStreamEvent
{
    internal RefinementMessageStreamEvent(string id, RefinementMessageResponse message)
        : base("refinement_message", id) => Message = message;

    public RefinementMessageResponse Message { get; }
}

/// <summary><c>{ type: 'charter_thinking', thinking }</c>.</summary>
public sealed record CharterThinkingStreamEvent : RequestStreamEvent
{
    internal CharterThinkingStreamEvent(string id, bool thinking)
        : base("charter_thinking", id) => Thinking = thinking;

    public bool Thinking { get; }
}

/// <summary><c>{ type: 'spec_proposed', spec }</c>.</summary>
public sealed record SpecProposedStreamEvent : RequestStreamEvent
{
    internal SpecProposedStreamEvent(string id, RequesterSpecResponse spec)
        : base("spec_proposed", id) => Spec = spec;

    public RequesterSpecResponse Spec { get; }
}

/// <summary><c>{ type: 'artifact', artifact }</c>.</summary>
public sealed record ArtifactStreamEvent : RequestStreamEvent
{
    internal ArtifactStreamEvent(string id, VerificationArtifactResponse artifact)
        : base("artifact", id) => Artifact = artifact;

    public VerificationArtifactResponse Artifact { get; }
}

/// <summary><c>{ type: 'artifact_state', artifactId, state, expiresAt? }</c>.</summary>
public sealed record ArtifactStateStreamEvent : RequestStreamEvent
{
    internal ArtifactStateStreamEvent(
        string id,
        string artifactId,
        ApiArtifactState state,
        DateTimeOffset? expiresAt)
        : base("artifact_state", id)
    {
        ArtifactId = artifactId;
        State = state;
        ExpiresAt = expiresAt;
    }

    public string ArtifactId { get; }

    public ApiArtifactState State { get; }

    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary><c>{ type: 'failed', failureSummary }</c>.</summary>
public sealed record FailedStreamEvent : RequestStreamEvent
{
    internal FailedStreamEvent(string id, string failureSummary)
        : base("failed", id) => FailureSummary = failureSummary;

    public string FailureSummary { get; }
}

/// <summary><c>{ type: 'ended', endedAt }</c>.</summary>
public sealed record EndedStreamEvent : RequestStreamEvent
{
    internal EndedStreamEvent(string id, DateTimeOffset endedAt)
        : base("ended", id) => EndedAt = endedAt;

    public DateTimeOffset EndedAt { get; }
}
