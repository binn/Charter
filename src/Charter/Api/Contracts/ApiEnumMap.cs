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
        RequestStatus.NoChangesNeeded => ApiRequestStatus.NoChangesNeeded,
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

    /// <summary>Section 12.</summary>
    public static ApiThemePreference ToApi(this ThemePreference theme) => theme switch
    {
        ThemePreference.System => ApiThemePreference.System,
        ThemePreference.Light => ApiThemePreference.Light,
        ThemePreference.Dark => ApiThemePreference.Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme preference."),
    };

    /// <summary>Section 12, in the other direction.</summary>
    public static ThemePreference ToDomain(this ApiThemePreference theme) => theme switch
    {
        ApiThemePreference.System => ThemePreference.System,
        ApiThemePreference.Light => ThemePreference.Light,
        ApiThemePreference.Dark => ThemePreference.Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme preference."),
    };

    /// <summary>Section 12.</summary>
    public static ApiPanePreference ToApi(this PanePreference pane) => pane switch
    {
        PanePreference.Simple => ApiPanePreference.Simple,
        PanePreference.Detailed => ApiPanePreference.Detailed,
        PanePreference.Developer => ApiPanePreference.Developer,
        _ => throw new ArgumentOutOfRangeException(nameof(pane), pane, "Unknown pane preference."),
    };

    /// <summary>Section 12, in the other direction.</summary>
    public static PanePreference ToDomain(this ApiPanePreference pane) => pane switch
    {
        ApiPanePreference.Simple => PanePreference.Simple,
        ApiPanePreference.Detailed => PanePreference.Detailed,
        ApiPanePreference.Developer => PanePreference.Developer,
        _ => throw new ArgumentOutOfRangeException(nameof(pane), pane, "Unknown pane preference."),
    };

    /// <summary>Section 11. Two buttons.</summary>
    public static ApiFeedbackVerdict ToApi(this FeedbackVerdict verdict) => verdict switch
    {
        FeedbackVerdict.Works => ApiFeedbackVerdict.Works,
        FeedbackVerdict.NotQuite => ApiFeedbackVerdict.NotQuite,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown feedback verdict."),
    };

    /// <summary>Section 11, in the other direction.</summary>
    public static FeedbackVerdict ToDomain(this ApiFeedbackVerdict verdict) => verdict switch
    {
        ApiFeedbackVerdict.Works => FeedbackVerdict.Works,
        ApiFeedbackVerdict.NotQuite => FeedbackVerdict.NotQuite,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown feedback verdict."),
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

    /// <summary>Section 33.2.</summary>
    public static ApiAgentMode ToApi(this RunnerAgentMode mode) => mode switch
    {
        RunnerAgentMode.Docker => ApiAgentMode.Docker,
        RunnerAgentMode.Native => ApiAgentMode.Native,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown agent mode."),
    };

    /// <summary>
    /// Section 33.3.
    /// </summary>
    /// <remarks>
    /// <c>draining</c> has no domain counterpart: an agent that has stopped heartbeating but still
    /// holds leases is <c>online</c> in the column and offline in fact, and the runners list has to
    /// say which. That distinction is made by the caller, which knows the clock, rather than here.
    /// </remarks>
    public static ApiAgentStatus ToApi(this RunnerAgentStatus status) => status switch
    {
        RunnerAgentStatus.Online => ApiAgentStatus.Online,
        RunnerAgentStatus.Offline => ApiAgentStatus.Offline,
        RunnerAgentStatus.Revoked => ApiAgentStatus.Revoked,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent status."),
    };
}
