using System.Globalization;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Builds the kind-specific body of the verification artifact card (section 27.7).
/// </summary>
/// <remarks>
/// <para>
/// The stored artifact carries a URL, a storage reference, a connect string and a jsonb
/// <see cref="VerificationArtifact.Payload"/>. The first three say <em>where</em> the artifact is;
/// the payload says <em>what</em> it is — the checksum, the size, the capture list, the pass/fail
/// counts, the device a hardware-in-the-loop run used.
/// </para>
/// <para>
/// <strong>Nothing here invents a value.</strong> Where the payload is absent or does not carry a
/// field, the field is left at its empty value rather than filled in from a plausible guess. A blank
/// checksum reads as "not recorded"; a fabricated one reads as a verified fact, and would be worse
/// than useless on a card whose whole job is letting a person judge whether the change is right. The
/// only values derived rather than read are ones the URL or filename genuinely determines — a
/// platform from a <c>.apk</c> extension, a provider from a TestFlight link — and a payload that
/// states them explicitly wins.
/// </para>
/// <para>
/// A payload that will not parse is treated as absent. A runner that writes malformed JSON should
/// produce a card missing detail, not a request detail endpoint that returns 500.
/// </para>
/// </remarks>
public static class ArtifactPayloads
{
    private static readonly JsonSerializerOptions Options = CharterApiJson.Options;

    /// <summary>The payload for one artifact.</summary>
    public static object For(VerificationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var stored = Read(artifact.Payload);

        return artifact.Kind switch
        {
            VerificationArtifactKind.HostedPreview => HostedPreview(artifact, stored),
            VerificationArtifactKind.BuildArtifact => BuildArtifact(artifact, stored),
            VerificationArtifactKind.DistributionChannel => DistributionChannel(artifact, stored),
            VerificationArtifactKind.Capture => Capture(artifact, stored),
            VerificationArtifactKind.EphemeralInstance => EphemeralInstance(artifact, stored),
            VerificationArtifactKind.TestReport => TestReport(artifact, stored),
            VerificationArtifactKind.HilReport => HilReport(artifact, stored),
            VerificationArtifactKind.None => new NoVerificationPayload
            {
                Explanation = Clean(stored?.Explanation)
                    ?? (artifact.InstructionsMd is { Length: > 0 } explanation
                        ? explanation
                        : RequestPresentation.NoVerificationExplanation),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact.Kind, "Unknown artifact kind."),
        };
    }

    private static HostedPreviewPayload HostedPreview(VerificationArtifact artifact, StoredPayload? stored)
    {
        var url = Clean(stored?.Url) ?? artifact.Url ?? string.Empty;

        return new HostedPreviewPayload
        {
            Url = url,
            DisplayUrl = Clean(stored?.DisplayUrl) ?? RequestPresentation.TruncateUrl(url),

            // "Unknown" is the honest answer for a dot whose whole purpose is to say whether anybody
            // has checked. Only a probe that actually ran writes anything else here.
            Reachability = stored?.Reachability ?? ApiReachability.Unknown,
        };
    }

    private static BuildArtifactPayload BuildArtifact(VerificationArtifact artifact, StoredPayload? stored)
    {
        var reference = artifact.FileRef ?? string.Empty;
        var slash = reference.LastIndexOf('/');
        var derived = slash >= 0 && slash < reference.Length - 1 ? reference[(slash + 1)..] : reference;
        var filename = Clean(stored?.Filename) ?? derived;

        return new BuildArtifactPayload
        {
            Platform = stored?.Platform ?? Platform(filename),
            Filename = filename,

            // Zero means "not recorded". The card is expected to say so rather than render "0 B".
            SizeBytes = stored?.SizeBytes ?? 0,
            ChecksumAlgorithm = Clean(stored?.ChecksumAlgorithm) ?? "sha256",
            ChecksumShort = Clean(stored?.ChecksumShort) ?? string.Empty,
            DownloadUrl = Clean(stored?.DownloadUrl) ?? artifact.Url ?? string.Empty,
            InstallInstructionsMd = Clean(stored?.InstallInstructionsMd) ?? artifact.InstructionsMd,
        };
    }

    private static DistributionChannelPayload DistributionChannel(VerificationArtifact artifact, StoredPayload? stored)
    {
        var url = Clean(stored?.OpenUrl) ?? artifact.Url ?? string.Empty;
        var provider = stored?.Provider ?? Provider(url);

        return new DistributionChannelPayload
        {
            Provider = provider,
            ChannelName = Clean(stored?.ChannelName) ?? ChannelName(provider),
            BuildNumber = Clean(stored?.BuildNumber),
            OpenUrl = url,
            InviteRequired = stored?.InviteRequired ?? false,
            InviteNote = Clean(stored?.InviteNote),
        };
    }

    private static CapturePayload Capture(VerificationArtifact artifact, StoredPayload? stored)
    {
        if (stored?.Items is not { Count: > 0 } items)
        {
            return new CapturePayload { Items = [] };
        }

        var captures = new List<CaptureItemResponse>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            if (Clean(item.Url) is not { } url)
            {
                // A capture with nowhere to load from is not a capture. Dropping it beats rendering a
                // broken image in the component that is supposed to show the change working.
                continue;
            }

            captures.Add(new CaptureItemResponse
            {
                Id = Clean(item.Id) ?? Identifier(artifact.Id, "capture", index),
                MediaType = item.MediaType ?? ApiCaptureMediaType.Image,
                Url = url,
                PosterUrl = Clean(item.PosterUrl),
                Caption = Clean(item.Caption) ?? string.Empty,
                BaselineUrl = Clean(item.BaselineUrl),
                Width = item.Width,
                Height = item.Height,
            });
        }

        return new CapturePayload { Items = captures };
    }

    private static EphemeralInstancePayload EphemeralInstance(VerificationArtifact artifact, StoredPayload? stored)
    {
        var connect = Clean(stored?.ConnectString) ?? artifact.ConnectString ?? artifact.Url ?? string.Empty;
        var scheme = connect.IndexOf("://", StringComparison.Ordinal);

        return new EphemeralInstancePayload
        {
            Protocol = Clean(stored?.Protocol) ?? (scheme > 0 ? connect[..scheme] : "tcp"),
            ConnectString = connect,
            Region = Clean(stored?.Region),
            Note = Clean(stored?.Note) ?? artifact.InstructionsMd,
        };
    }

    private static TestReportPayload TestReport(VerificationArtifact artifact, StoredPayload? stored)
    {
        var failures = new List<TestFailureResponse>();

        if (stored?.Failures is { Count: > 0 } recordedFailures)
        {
            for (var index = 0; index < recordedFailures.Count; index++)
            {
                var failure = recordedFailures[index];

                failures.Add(new TestFailureResponse
                {
                    Id = Clean(failure.Id) ?? Identifier(artifact.Id, "failure", index),
                    Name = Clean(failure.Name) ?? "An unnamed test",
                    Suite = Clean(failure.Suite),

                    // Section 27.7: "shown expanded, never re-worded."
                    Assertion = failure.Assertion ?? string.Empty,
                });
            }
        }

        return new TestReportPayload
        {
            Passed = stored?.Passed ?? 0,
            Failed = stored?.Failed ?? 0,
            Skipped = stored?.Skipped ?? 0,
            DurationMs = stored?.DurationMs ?? 0,
            ReportUrl = Clean(stored?.ReportUrl) ?? Clean(artifact.Url),
            Failures = failures,
        };
    }

    private static HilReportPayload HilReport(VerificationArtifact artifact, StoredPayload? stored)
    {
        var traces = new List<HilTraceResponse>();

        if (stored?.Traces is { Count: > 0 } recordedTraces)
        {
            for (var index = 0; index < recordedTraces.Count; index++)
            {
                var trace = recordedTraces[index];

                traces.Add(new HilTraceResponse
                {
                    Id = Clean(trace.Id) ?? Identifier(artifact.Id, "trace", index),
                    Label = Clean(trace.Label) ?? "Captured signal",
                    ImageUrl = Clean(trace.ImageUrl),
                    Summary = Clean(trace.Summary),
                });
            }
        }

        return new HilReportPayload
        {
            DeviceId = Clean(stored?.DeviceId) ?? string.Empty,
            DeviceLabel = Clean(stored?.DeviceLabel) ?? Clean(stored?.DeviceId) ?? string.Empty,
            RunDurationMs = stored?.RunDurationMs ?? 0,

            // Section 27.7 pairs every state with an icon and a label, so a run whose outcome was
            // never recorded must not default to "pass" — the failed state is the safe unknown here.
            Outcome = stored?.Outcome ?? ApiHilOutcome.Fail,
            Traces = traces,
            ReportUrl = Clean(stored?.ReportUrl) ?? Clean(artifact.Url),
        };
    }

    private static StoredPayload? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredPayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiBuildPlatform Platform(string filename)
    {
        var lowered = filename.ToLowerInvariant();

        return lowered switch
        {
            _ when lowered.EndsWith(".apk", StringComparison.Ordinal)
                   || lowered.EndsWith(".aab", StringComparison.Ordinal) => ApiBuildPlatform.Android,
            _ when lowered.EndsWith(".ipa", StringComparison.Ordinal) => ApiBuildPlatform.Ios,
            _ when lowered.EndsWith(".exe", StringComparison.Ordinal)
                   || lowered.EndsWith(".msi", StringComparison.Ordinal) => ApiBuildPlatform.Windows,
            _ when lowered.EndsWith(".dmg", StringComparison.Ordinal)
                   || lowered.EndsWith(".app", StringComparison.Ordinal) => ApiBuildPlatform.Macos,
            _ when lowered.EndsWith(".appimage", StringComparison.Ordinal)
                   || lowered.EndsWith(".deb", StringComparison.Ordinal)
                   || lowered.EndsWith(".rpm", StringComparison.Ordinal) => ApiBuildPlatform.Linux,
            _ when lowered.EndsWith(".elf", StringComparison.Ordinal)
                   || lowered.EndsWith(".uf2", StringComparison.Ordinal)
                   || lowered.EndsWith(".hex", StringComparison.Ordinal) => ApiBuildPlatform.Embedded,
            _ => ApiBuildPlatform.Other,
        };
    }

    private static ApiDistributionProvider Provider(string url)
    {
        var lowered = url.ToLowerInvariant();

        return lowered switch
        {
            _ when lowered.Contains("testflight", StringComparison.Ordinal) => ApiDistributionProvider.Testflight,
            _ when lowered.Contains("play.google", StringComparison.Ordinal) => ApiDistributionProvider.PlayInternal,
            _ when lowered.Contains("firebase", StringComparison.Ordinal) => ApiDistributionProvider.Firebase,
            _ when lowered.Contains("expo", StringComparison.Ordinal) => ApiDistributionProvider.ExpoEas,
            _ => ApiDistributionProvider.Other,
        };
    }

    private static string ChannelName(ApiDistributionProvider provider) => provider switch
    {
        ApiDistributionProvider.Testflight => "TestFlight",
        ApiDistributionProvider.PlayInternal => "Play Internal Testing",
        ApiDistributionProvider.Firebase => "Firebase App Distribution",
        ApiDistributionProvider.ExpoEas => "Expo EAS Update",
        _ => "Install",
    };

    /// <summary>A stable id for a nested item the payload did not name, derived from the artifact.</summary>
    private static string Identifier(Guid artifactId, string part, int index)
        => string.Create(CultureInfo.InvariantCulture, $"{artifactId:N}-{part}-{index + 1}");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Everything <c>verification_artifacts.payload</c> may carry, across all eight kinds.
    /// </summary>
    /// <remarks>
    /// One flat document rather than eight, because the column holds one kind's body at a time and a
    /// discriminated read would need the kind twice — once in the row and once in the JSON — with
    /// nothing keeping the two honest. Every member is optional: a runner that records a checksum but
    /// not a size produces a card with a checksum and no size, which is the truth.
    /// </remarks>
    private sealed record StoredPayload
    {
        public string? Url { get; init; }

        public string? DisplayUrl { get; init; }

        public ApiReachability? Reachability { get; init; }

        public ApiBuildPlatform? Platform { get; init; }

        public string? Filename { get; init; }

        public long? SizeBytes { get; init; }

        public string? ChecksumAlgorithm { get; init; }

        public string? ChecksumShort { get; init; }

        public string? DownloadUrl { get; init; }

        public string? InstallInstructionsMd { get; init; }

        public ApiDistributionProvider? Provider { get; init; }

        public string? ChannelName { get; init; }

        public string? BuildNumber { get; init; }

        public string? OpenUrl { get; init; }

        public bool? InviteRequired { get; init; }

        public string? InviteNote { get; init; }

        public IReadOnlyList<StoredCaptureItem>? Items { get; init; }

        public string? Protocol { get; init; }

        public string? ConnectString { get; init; }

        public string? Region { get; init; }

        public string? Note { get; init; }

        public int? Passed { get; init; }

        public int? Failed { get; init; }

        public int? Skipped { get; init; }

        public long? DurationMs { get; init; }

        public string? ReportUrl { get; init; }

        public IReadOnlyList<StoredTestFailure>? Failures { get; init; }

        public string? DeviceId { get; init; }

        public string? DeviceLabel { get; init; }

        public long? RunDurationMs { get; init; }

        public ApiHilOutcome? Outcome { get; init; }

        public IReadOnlyList<StoredHilTrace>? Traces { get; init; }

        public string? Explanation { get; init; }
    }

    private sealed record StoredCaptureItem
    {
        public string? Id { get; init; }

        public ApiCaptureMediaType? MediaType { get; init; }

        public string? Url { get; init; }

        public string? PosterUrl { get; init; }

        public string? Caption { get; init; }

        public string? BaselineUrl { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }
    }

    private sealed record StoredTestFailure
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Suite { get; init; }

        public string? Assertion { get; init; }
    }

    private sealed record StoredHilTrace
    {
        public string? Id { get; init; }

        public string? Label { get; init; }

        public string? ImageUrl { get; init; }

        public string? Summary { get; init; }
    }
}
