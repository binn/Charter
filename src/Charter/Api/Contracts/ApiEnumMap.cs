using Charter.Domain;

namespace Charter.Api.Contracts;

/// <summary>
/// The single crossing point between the domain vocabularies and the wire vocabularies.
/// </summary>
/// <remarks>
/// Every <c>switch</c> here is exhaustive and throws on an unknown value rather than defaulting.
/// A new domain state therefore surfaces as a loud failure in one file instead of quietly
/// serialising as something the SPA's union has never heard of.
/// </remarks>
public static class ApiEnumMap
{
    /// <summary>Section 6.</summary>
    public static ApiRequestStatus ToApi(this RequestStatus status) => status switch
    {
        RequestStatus.Draft => ApiRequestStatus.Draft,
        RequestStatus.Refining => ApiRequestStatus.Refining,
        RequestStatus.SpecReady => ApiRequestStatus.SpecReady,
        RequestStatus.Rejected => ApiRequestStatus.Rejected,
        RequestStatus.Queued => ApiRequestStatus.Queued,
        RequestStatus.Running => ApiRequestStatus.Running,
        RequestStatus.NeedsInput => ApiRequestStatus.NeedsInput,
        RequestStatus.PrOpen => ApiRequestStatus.PrOpen,
        RequestStatus.PreviewReady => ApiRequestStatus.PreviewReady,
        RequestStatus.InReview => ApiRequestStatus.InReview,
        RequestStatus.Merged => ApiRequestStatus.Merged,
        RequestStatus.Failed => ApiRequestStatus.Failed,
        RequestStatus.Cancelled => ApiRequestStatus.Cancelled,
        RequestStatus.Stale => ApiRequestStatus.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown request status."),
    };

    /// <summary>Section 7.1.</summary>
    public static ApiRole ToApi(this MemberRole role) => role switch
    {
        MemberRole.Requester => ApiRole.Requester,
        MemberRole.Approver => ApiRole.Approver,
        MemberRole.Engineer => ApiRole.Engineer,
        MemberRole.Admin => ApiRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown member role."),
    };

    /// <summary>Section 13.</summary>
    public static ApiTeachingLevel ToApi(this TeachingLevel level) => level switch
    {
        TeachingLevel.ExplainEverything => ApiTeachingLevel.ExplainEverything,
        TeachingLevel.SkipTheBasics => ApiTeachingLevel.SkipTheBasics,
        TeachingLevel.JustTheDecisions => ApiTeachingLevel.JustTheDecisions,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown teaching level."),
    };

    /// <summary>Section 13, in the other direction.</summary>
    public static TeachingLevel ToDomain(this ApiTeachingLevel level) => level switch
    {
        ApiTeachingLevel.ExplainEverything => TeachingLevel.ExplainEverything,
        ApiTeachingLevel.SkipTheBasics => TeachingLevel.SkipTheBasics,
        ApiTeachingLevel.JustTheDecisions => TeachingLevel.JustTheDecisions,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown teaching level."),
    };

    /// <summary>Section 27.1.</summary>
    public static ApiArtifactKind ToApi(this VerificationArtifactKind kind) => kind switch
    {
        VerificationArtifactKind.HostedPreview => ApiArtifactKind.HostedPreview,
        VerificationArtifactKind.BuildArtifact => ApiArtifactKind.BuildArtifact,
        VerificationArtifactKind.DistributionChannel => ApiArtifactKind.DistributionChannel,
        VerificationArtifactKind.Capture => ApiArtifactKind.Capture,
        VerificationArtifactKind.EphemeralInstance => ApiArtifactKind.EphemeralInstance,
        VerificationArtifactKind.TestReport => ApiArtifactKind.TestReport,
        VerificationArtifactKind.HilReport => ApiArtifactKind.HilReport,
        VerificationArtifactKind.None => ApiArtifactKind.None,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artifact kind."),
    };

    /// <summary>Section 27.7.</summary>
    public static ApiArtifactState ToApi(this VerificationArtifactState state) => state switch
    {
        VerificationArtifactState.Pending => ApiArtifactState.Pending,
        VerificationArtifactState.Ready => ApiArtifactState.Ready,
        VerificationArtifactState.Expiring => ApiArtifactState.Expiring,
        VerificationArtifactState.Expired => ApiArtifactState.Expired,
        VerificationArtifactState.Failed => ApiArtifactState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown artifact state."),
    };

    /// <summary>Section 27.7.</summary>
    public static ApiArtifactAudience ToApi(this VerificationArtifactAudience audience) => audience switch
    {
        VerificationArtifactAudience.Requester => ApiArtifactAudience.Requester,
        VerificationArtifactAudience.EngineerOnly => ApiArtifactAudience.EngineerOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown artifact audience."),
    };

    /// <summary>Section 2.2. Shown only inside the engineer details block.</summary>
    public static string ToApi(this RunnerKind runner) => runner switch
    {
        RunnerKind.Agent => "agent",
        RunnerKind.GitHubActions => "github-actions",
        RunnerKind.Docker => "docker",
        _ => throw new ArgumentOutOfRangeException(nameof(runner), runner, "Unknown runner."),
    };
}
