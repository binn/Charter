namespace Charter.Api.Contracts;

/*
 * The wire vocabularies, spelled exactly as ClientApp/src/api/types.ts declares them.
 *
 * These are deliberately separate from the domain enums. The domain is free to gain a state or
 * rename one; the wire contract is a published shape the SPA compiles against, and a rename that
 * silently changed a JSON literal would be a breaking change nobody noticed. The mapping between the
 * two lives in one place per enum (see ApiEnumMap), so a new domain state fails to compile rather
 * than serialising as something the client has never heard of.
 */

/// <summary>Section 7.1. Additive — a member may hold several.</summary>
public enum ApiRole
{
    Requester,
    Approver,
    Engineer,
    Admin,
}

/// <summary>Section 13. Named for what the reader wants, never for what they lack.</summary>
public enum ApiTeachingLevel
{
    ExplainEverything,
    SkipTheBasics,
    JustTheDecisions,
}

/// <summary>Section 12. Named for the user: Simple / Detailed / Developer.</summary>
public enum ApiPanePreference
{
    Simple,
    Detailed,
    Developer,
}

/// <summary>The three theme settings the shell offers.</summary>
public enum ApiThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>The state machine of section 6, as the requester's thread reads it.</summary>
public enum ApiRequestStatus
{
    Draft,
    Refining,
    SpecReady,
    Rejected,
    Queued,
    Running,
    NeedsInput,
    PrOpen,
    PreviewReady,
    InReview,
    Merged,
    Failed,
    Cancelled,
    Stale,
}

/// <summary>Section 10b. Modes of one conversation surface, promotable without losing history.</summary>
public enum ApiRefinementMode
{
    Chat,
    Plan,
    Build,
}

/// <summary>Who authored a turn of the refinement conversation.</summary>
public enum ApiRefinementAuthor
{
    Requester,
    Charter,
}

/// <summary>What kind of turn it is (section 10).</summary>
public enum ApiRefinementMessageKind
{
    Message,
    Question,
    Refusal,
    SpecProposed,
}

/// <summary>Section 11. The promoted, requester-facing vocabulary, plus the thread-level entries.</summary>
public enum ApiMilestoneKind
{
    Understanding,
    Changing,
    Checking,
    Assembling,
    Status,
    Question,
    Outcome,
}

/// <summary>How a milestone is rendering right now.</summary>
public enum ApiMilestoneState
{
    Active,
    Done,
    Failed,
}

/// <summary>Section 11. Two buttons. Do not make them write a bug report.</summary>
public enum ApiFeedbackVerdict
{
    Works,
    NotQuite,
}

/// <summary>Section 27.1.</summary>
public enum ApiArtifactKind
{
    HostedPreview,
    BuildArtifact,
    DistributionChannel,
    Capture,
    EphemeralInstance,
    TestReport,
    HilReport,
    None,
}

/// <summary>Section 27.7 card states.</summary>
public enum ApiArtifactState
{
    Pending,
    Ready,
    Expiring,
    Expired,
    Failed,
}

/// <summary>Who an artifact is for (section 27.7).</summary>
public enum ApiArtifactAudience
{
    Requester,
    EngineerOnly,
}

/// <summary>Platforms a downloadable build can target.</summary>
public enum ApiBuildPlatform
{
    Android,
    Ios,
    Windows,
    Macos,
    Linux,
    Embedded,
    Other,
}

/// <summary>Whether a hosted preview answered when Charter last looked.</summary>
public enum ApiReachability
{
    Checking,
    Reachable,
    Unreachable,
    Unknown,
}

/// <summary>Section 14: auth, migrations, money math and external calls float to the top.</summary>
public enum ApiChangeRisk
{
    High,
    Medium,
    Low,
}

/// <summary>Distribution channels a mobile build can land in.</summary>
public enum ApiDistributionProvider
{
    Testflight,
    PlayInternal,
    Firebase,
    ExpoEas,
    Other,
}

/// <summary>The verdict of a hardware-in-the-loop run.</summary>
public enum ApiHilOutcome
{
    Pass,
    Fail,
}

/// <summary>What a capture item holds.</summary>
public enum ApiCaptureMediaType
{
    Image,
    Video,
}
