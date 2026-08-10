namespace Charter.Api.Contracts;

/// <summary>
/// Section 27.7: change request number, commit SHA, branch, runner, duration and cost.
/// </summary>
/// <remarks>
/// <em>"<c>details</c> is omitted by the API, not hidden by CSS. Authorisation is not a rendering
/// concern."</em> The projection sets
/// <see cref="VerificationArtifactResponse.Details"/> to <c>null</c> for a viewer who may not see
/// repository identity and cost, and <see cref="CharterApiJson"/> then never writes the key.
/// Requesters never see a SHA.
/// </remarks>
public sealed record EngineerDetailsResponse
{
    public required int ChangeRequestNumber { get; init; }

    public required string ChangeRequestUrl { get; init; }

    /// <summary>
    /// What the provider calls it, so the UI reads correctly (change spec 001 part A.2): <em>pull
    /// request</em> on GitHub, <em>merge request</em> on GitLab.
    /// </summary>
    /// <remarks>
    /// Supplied by the provider rather than hard-coded in the client, and lower case, so a caller can
    /// sentence-case it where it starts a sentence. A client that ignores it renders Charter's own
    /// neutral wording, which is wrong rather than broken.
    /// </remarks>
    public string ChangeRequestTerm { get; init; } = "change request";

    /// <summary>
    /// The short form an engineer types: <c>PR</c> on GitHub, <c>MR</c> on GitLab. What the card's
    /// <c>PR #142</c> chip is built from, so the chip stops being GitHub-specific.
    /// </summary>
    public string ChangeRequestTermShort { get; init; } = "CR";

    public required string CommitSha { get; init; }

    public required string Branch { get; init; }

    public required string Runner { get; init; }

    /// <summary>How long the run took. Elapsed, never remaining (section 6).</summary>
    public required long DurationMs { get; init; }

    public required decimal CostUsd { get; init; }

    public string? RecapUrl { get; init; }
}

/// <summary>Section 27.7 <c>hosted_preview</c>.</summary>
public sealed record HostedPreviewPayload
{
    public required string Url { get; init; }

    /// <summary>Pre-truncated for the chip. <see cref="Url"/> is what gets opened and copied.</summary>
    public required string DisplayUrl { get; init; }

    public required ApiReachability Reachability { get; init; }
}

/// <summary>Section 27.7 <c>build_artifact</c>.</summary>
public sealed record BuildArtifactPayload
{
    public required ApiBuildPlatform Platform { get; init; }

    public required string Filename { get; init; }

    public required long SizeBytes { get; init; }

    public required string ChecksumAlgorithm { get; init; }

    /// <summary>Already shortened here; the card shows it verbatim.</summary>
    public required string ChecksumShort { get; init; }

    public required string DownloadUrl { get; init; }

    public string? InstallInstructionsMd { get; init; }
}

/// <summary>Section 27.7 <c>distribution_channel</c>.</summary>
public sealed record DistributionChannelPayload
{
    public required ApiDistributionProvider Provider { get; init; }

    public required string ChannelName { get; init; }

    public string? BuildNumber { get; init; }

    /// <summary>Deep link. Also what the QR encodes.</summary>
    public required string OpenUrl { get; init; }

    public required bool InviteRequired { get; init; }

    public string? InviteNote { get; init; }
}

/// <summary>One screenshot or clip.</summary>
public sealed record CaptureItemResponse
{
    public required string Id { get; init; }

    public required ApiCaptureMediaType MediaType { get; init; }

    public required string Url { get; init; }

    public string? PosterUrl { get; init; }

    public required string Caption { get; init; }

    /// <summary>Present when a baseline exists; enables the before/after toggle.</summary>
    public string? BaselineUrl { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }
}

/// <summary>Section 27.7 <c>capture</c>.</summary>
public sealed record CapturePayload
{
    public required IReadOnlyList<CaptureItemResponse> Items { get; init; }
}

/// <summary>Section 27.7 <c>ephemeral_instance</c>.</summary>
public sealed record EphemeralInstancePayload
{
    public required string Protocol { get; init; }

    public required string ConnectString { get; init; }

    public string? Region { get; init; }

    public string? Note { get; init; }
}

/// <summary>One failing test, as the runner emitted it.</summary>
public sealed record TestFailureResponse
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Suite { get; init; }

    /// <summary>Shown expanded, never re-worded.</summary>
    public required string Assertion { get; init; }
}

/// <summary>Section 27.7 <c>test_report</c>.</summary>
public sealed record TestReportPayload
{
    public required int Passed { get; init; }

    public required int Failed { get; init; }

    public required int Skipped { get; init; }

    public required long DurationMs { get; init; }

    public string? ReportUrl { get; init; }

    public required IReadOnlyList<TestFailureResponse> Failures { get; init; }
}

/// <summary>One captured signal from a hardware-in-the-loop run.</summary>
public sealed record HilTraceResponse
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public string? ImageUrl { get; init; }

    public string? Summary { get; init; }
}

/// <summary>Section 27.7 <c>hil_report</c>.</summary>
public sealed record HilReportPayload
{
    public required string DeviceId { get; init; }

    public required string DeviceLabel { get; init; }

    public required long RunDurationMs { get; init; }

    public required ApiHilOutcome Outcome { get; init; }

    public required IReadOnlyList<HilTraceResponse> Traces { get; init; }

    public string? ReportUrl { get; init; }
}

/// <summary>
/// Section 27.4: "do not pretend parity". A plain explanation that this change type is
/// engineer-verified and the requester will be told when it is live.
/// </summary>
public sealed record NoVerificationPayload
{
    public required string Explanation { get; init; }
}

/// <summary>
/// The payoff for the entire pipeline (section 27.1).
/// </summary>
/// <remarks>
/// <see cref="Payload"/> is declared as <see cref="object"/> so the serialiser writes the runtime
/// type: the SPA's <c>VerificationArtifact</c> is a union discriminated on <see cref="Kind"/> with a
/// kind-specific <c>payload</c>, and an extra wrapper property would not match it.
/// </remarks>
public sealed record VerificationArtifactResponse
{
    public required string Id { get; init; }

    public required ApiArtifactKind Kind { get; init; }

    public required ApiArtifactState State { get; init; }

    public required ApiArtifactAudience Audience { get; init; }

    /// <summary>Tab label when a session produced several. Short: "Preview", "TestFlight".</summary>
    public required string Label { get; init; }

    /// <summary>Exactly one artifact per session is primary; it is the first tab.</summary>
    public required bool Primary { get; init; }

    /// <summary>Section 27.5. Drives the countdown and the <c>expired</c> state.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Section 27.1 <c>instructions_md</c> — how to actually use this thing.</summary>
    public string? InstructionsMd { get; init; }

    /// <summary>Set when the artifact failed. Plain language, requester-safe (section 11).</summary>
    public string? FailureSummary { get; init; }

    public required object Payload { get; init; }

    /// <summary>Absent unless section 7.4 says this viewer may see repository identity and cost.</summary>
    public EngineerDetailsResponse? Details { get; init; }
}
