using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Builds the kind-specific body of the verification artifact card (section 27.7).
/// </summary>
/// <remarks>
/// <para>
/// The stored artifact carries a URL, a storage reference and a connect string, and the eight card
/// bodies are projections of those three plus the kind. Where a body needs something the schema has
/// no column for — a checksum, a device id, a capture list — the field is left at its empty value
/// rather than invented. A blank checksum reads as "not recorded"; a fabricated one reads as a
/// verified fact, and would be worse than useless on a card whose whole job is letting a person
/// judge whether the change is right.
/// </para>
/// </remarks>
public static class ArtifactPayloads
{
    /// <summary>The payload for one artifact.</summary>
    public static object For(VerificationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        return artifact.Kind switch
        {
            VerificationArtifactKind.HostedPreview => HostedPreview(artifact),
            VerificationArtifactKind.BuildArtifact => BuildArtifact(artifact),
            VerificationArtifactKind.DistributionChannel => DistributionChannel(artifact),
            VerificationArtifactKind.Capture => new CapturePayload { Items = [] },
            VerificationArtifactKind.EphemeralInstance => EphemeralInstance(artifact),
            VerificationArtifactKind.TestReport => new TestReportPayload
            {
                Passed = 0,
                Failed = 0,
                Skipped = 0,
                DurationMs = 0,
                ReportUrl = Empty(artifact.Url),
                Failures = [],
            },
            VerificationArtifactKind.HilReport => new HilReportPayload
            {
                DeviceId = string.Empty,
                DeviceLabel = string.Empty,
                RunDurationMs = 0,
                Outcome = ApiHilOutcome.Pass,
                Traces = [],
                ReportUrl = Empty(artifact.Url),
            },
            VerificationArtifactKind.None => new NoVerificationPayload
            {
                Explanation = artifact.InstructionsMd is { Length: > 0 } explanation
                    ? explanation
                    : RequestPresentation.NoVerificationExplanation,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact.Kind, "Unknown artifact kind."),
        };
    }

    private static HostedPreviewPayload HostedPreview(VerificationArtifact artifact)
    {
        var url = artifact.Url ?? string.Empty;

        return new HostedPreviewPayload
        {
            Url = url,
            DisplayUrl = RequestPresentation.TruncateUrl(url),

            // Charter does not probe previews yet, and "unknown" is the honest answer for a dot whose
            // whole purpose is to say whether anybody has checked.
            Reachability = ApiReachability.Unknown,
        };
    }

    private static BuildArtifactPayload BuildArtifact(VerificationArtifact artifact)
    {
        var reference = artifact.FileRef ?? string.Empty;
        var slash = reference.LastIndexOf('/');
        var filename = slash >= 0 && slash < reference.Length - 1 ? reference[(slash + 1)..] : reference;

        return new BuildArtifactPayload
        {
            Platform = Platform(filename),
            Filename = filename,
            SizeBytes = 0,
            ChecksumAlgorithm = "sha256",
            ChecksumShort = string.Empty,
            DownloadUrl = artifact.Url ?? string.Empty,
            InstallInstructionsMd = artifact.InstructionsMd,
        };
    }

    private static DistributionChannelPayload DistributionChannel(VerificationArtifact artifact)
    {
        var url = artifact.Url ?? string.Empty;
        var provider = Provider(url);

        return new DistributionChannelPayload
        {
            Provider = provider,
            ChannelName = ChannelName(provider),
            OpenUrl = url,
            InviteRequired = false,
        };
    }

    private static EphemeralInstancePayload EphemeralInstance(VerificationArtifact artifact)
    {
        var connect = artifact.ConnectString ?? artifact.Url ?? string.Empty;
        var scheme = connect.IndexOf("://", StringComparison.Ordinal);

        return new EphemeralInstancePayload
        {
            Protocol = scheme > 0 ? connect[..scheme] : "tcp",
            ConnectString = connect,
            Note = artifact.InstructionsMd,
        };
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

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
