using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// The verification artifact card (section 27.7) — the single most-looked-at component in Charter.
/// </summary>
/// <remarks>
/// Two rules are being checked, and they pull in opposite directions. The card has to carry real
/// detail: a checksum, a size, a device identifier, a pass/fail count, or the body is a shell around
/// a link. And it must never carry <em>invented</em> detail, because the card's entire job is letting
/// somebody judge whether the change is right — a fabricated checksum is worse than a missing one.
/// </remarks>
public class ApiArtifactTests
{
    private static VerificationArtifact Ready(
        VerificationArtifactKind kind,
        string? payload = null,
        string? url = null,
        string? fileRef = null)
    {
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), kind);

        artifact.MarkReady(
            url: url ?? (kind == VerificationArtifactKind.None ? null : "https://example.invalid/thing"),
            fileRef: fileRef,
            payload: payload);

        return artifact;
    }

    [Fact]
    public void ABuildArtifactWithNoRecordedDetailReportsNothingRatherThanGuessing()
    {
        var payload = Assert.IsType<BuildArtifactPayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.BuildArtifact,
                fileRef: "sessions/42/app-release.apk")));

        // The platform is genuinely determined by the extension; nothing else is.
        Assert.Equal(ApiBuildPlatform.Android, payload.Platform);
        Assert.Equal("app-release.apk", payload.Filename);
        Assert.Equal(0, payload.SizeBytes);
        Assert.Equal(string.Empty, payload.ChecksumShort);
    }

    [Fact]
    public void ABuildArtifactRendersTheChecksumAndSizeThatWereRecorded()
    {
        var payload = Assert.IsType<BuildArtifactPayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.BuildArtifact,
                """
                {
                  "filename": "northbeam-1.4.2.ipa",
                  "sizeBytes": 41238912,
                  "checksumAlgorithm": "sha256",
                  "checksumShort": "a3f9c21d",
                  "installInstructionsMd": "Open on the device."
                }
                """,
                fileRef: "sessions/42/app-release.apk")));

        Assert.Equal(ApiBuildPlatform.Ios, payload.Platform);
        Assert.Equal("northbeam-1.4.2.ipa", payload.Filename);
        Assert.Equal(41_238_912, payload.SizeBytes);
        Assert.Equal("a3f9c21d", payload.ChecksumShort);
        Assert.Equal("Open on the device.", payload.InstallInstructionsMd);
    }

    [Fact]
    public void ACaptureCarriesItsItemsAndDropsOnesWithNowhereToLoadFrom()
    {
        var payload = Assert.IsType<CapturePayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.Capture,
                """
                {
                  "items": [
                    {
                      "mediaType": "image",
                      "url": "https://example.invalid/after.png",
                      "baselineUrl": "https://example.invalid/before.png",
                      "caption": "The vertical is pre-selected",
                      "width": 1280,
                      "height": 720
                    },
                    { "mediaType": "video", "caption": "no url at all" }
                  ]
                }
                """)));

        var item = Assert.Single(payload.Items);

        Assert.Equal(ApiCaptureMediaType.Image, item.MediaType);
        Assert.Equal("The vertical is pre-selected", item.Caption);
        Assert.Equal("https://example.invalid/before.png", item.BaselineUrl);
        Assert.Equal(1280, item.Width);

        // The id is derived from the artifact, so a refetch and a replayed stream frame agree.
        Assert.NotEmpty(item.Id);
    }

    [Fact]
    public void ATestReportCarriesItsCountsAndItsFailuresVerbatim()
    {
        var payload = Assert.IsType<TestReportPayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.TestReport,
                """
                {
                  "passed": 214,
                  "failed": 1,
                  "skipped": 3,
                  "durationMs": 91240,
                  "failures": [
                    {
                      "name": "QuoteWizardTests.RemembersVertical",
                      "suite": "Quotes",
                      "assertion": "Expected: Solar\nActual: null"
                    }
                  ]
                }
                """)));

        Assert.Equal(214, payload.Passed);
        Assert.Equal(1, payload.Failed);
        Assert.Equal(91_240, payload.DurationMs);

        var failure = Assert.Single(payload.Failures);
        Assert.Equal("Quotes", failure.Suite);

        // Section 27.7: "shown expanded, never re-worded."
        Assert.Equal("Expected: Solar\nActual: null", failure.Assertion);
    }

    [Fact]
    public void AHardwareRunCarriesItsDeviceAndItsTraces()
    {
        var payload = Assert.IsType<HilReportPayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.HilReport,
                """
                {
                  "deviceId": "nrf52840-dk-0042",
                  "deviceLabel": "nRF52840 DK #42",
                  "runDurationMs": 18400,
                  "outcome": "pass",
                  "traces": [{ "label": "SPI clock", "imageUrl": "https://example.invalid/scope.png" }]
                }
                """)));

        Assert.Equal("nrf52840-dk-0042", payload.DeviceId);
        Assert.Equal("nRF52840 DK #42", payload.DeviceLabel);
        Assert.Equal(ApiHilOutcome.Pass, payload.Outcome);

        var trace = Assert.Single(payload.Traces);
        Assert.Equal("SPI clock", trace.Label);
    }

    [Fact]
    public void AHardwareRunWithNoRecordedOutcomeDoesNotClaimItPassed()
    {
        // Section 27.7 pairs every state with an icon and a label, and the one thing a card must not
        // do is report a pass nobody observed.
        var payload = Assert.IsType<HilReportPayload>(
            ArtifactPayloads.For(Ready(VerificationArtifactKind.HilReport)));

        Assert.Equal(ApiHilOutcome.Fail, payload.Outcome);
        Assert.Equal(string.Empty, payload.DeviceId);
        Assert.Empty(payload.Traces);
    }

    [Fact]
    public void AMalformedPayloadDegradesToNoDetailRatherThanToAnError()
    {
        // A runner that writes broken JSON should cost a card its detail, not cost the request detail
        // endpoint a 500.
        var payload = Assert.IsType<HostedPreviewPayload>(
            ArtifactPayloads.For(Ready(
                VerificationArtifactKind.HostedPreview,
                "{ this is not json",
                url: "https://pr-142.preview.invalid/quotes/new")));

        Assert.Equal("https://pr-142.preview.invalid/quotes/new", payload.Url);
        Assert.Equal(ApiReachability.Unknown, payload.Reachability);
    }

    [Fact]
    public void AnUnprobedPreviewSaysUnknownRatherThanReachable()
    {
        var payload = Assert.IsType<HostedPreviewPayload>(
            ArtifactPayloads.For(Ready(VerificationArtifactKind.HostedPreview)));

        Assert.Equal(ApiReachability.Unknown, payload.Reachability);
    }

    [Fact]
    public async Task TheEngineerDetailsNameTheBranchAndTheRequesterStillSeesNoneOfIt()
    {
        var scenario = ApiScenario.Build();

        var engineer = await scenario.RenderDetailAsync(scenario.Engineer);
        using var document = JsonDocument.Parse(engineer);

        var details = document.RootElement.GetProperty("artifacts")[0].GetProperty("details");

        // Section 27.7: the `Details` disclosure is "PR number, commit SHA, branch, runner, duration
        // and cost". The branch was the one that had nowhere to come from.
        Assert.Equal(ApiScenario.HeadBranch, details.GetProperty("branch").GetString());
        Assert.Equal(142, details.GetProperty("changeRequestNumber").GetInt32());

        // Change spec 001 part A.2: the word comes from the provider, so the same payload reads
        // "merge request" on GitLab without the client changing.
        Assert.Equal("pull request", details.GetProperty("changeRequestTerm").GetString());
        Assert.Equal("PR", details.GetProperty("changeRequestTermShort").GetString());

        var requester = await scenario.RenderDetailAsync(scenario.Requester);
        Assert.DoesNotContain("branch", ApiPayloads.Keys(requester));
        Assert.DoesNotContain(ApiScenario.HeadBranch, requester, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCardCarriesTheRecordedPayloadThroughTheWholeProjection()
    {
        var scenario = ApiScenario.Build(
            artifactPayload: """{ "displayUrl": "pr-142.preview.northbeam", "reachability": "reachable" }""");

        var body = await scenario.RenderDetailAsync(scenario.Requester);
        using var document = JsonDocument.Parse(body);

        var payload = document.RootElement.GetProperty("artifacts")[0].GetProperty("payload");

        Assert.Equal("pr-142.preview.northbeam", payload.GetProperty("displayUrl").GetString());
        Assert.Equal("reachable", payload.GetProperty("reachability").GetString());
    }

    [Fact]
    public async Task TheThreadCarriesTheVerdictWhenOneWasGiven()
    {
        var scenario = ApiScenario.Build(
            feedback: RequestFeedback.Record(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                FeedbackVerdict.Works,
                now: DateTimeOffset.UtcNow));

        var body = await scenario.RenderDetailAsync(scenario.Requester);
        using var document = JsonDocument.Parse(body);

        var feedback = document.RootElement.GetProperty("thread").GetProperty("feedback");
        Assert.Equal("works", feedback.GetProperty("verdict").GetString());

        // Section 11: two buttons only, and no note is a normal answer — the key is absent, not null.
        Assert.False(feedback.TryGetProperty("note", out _));

        // And with nobody having pressed either button, the whole block is absent.
        var untouched = await ApiScenario.Build().RenderDetailAsync(scenario.Requester);
        using var quiet = JsonDocument.Parse(untouched);
        Assert.False(quiet.RootElement.GetProperty("thread").TryGetProperty("feedback", out _));
    }
}
