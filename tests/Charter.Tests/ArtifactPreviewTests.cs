using System.Text.Json;
using Charter.Api;
using Charter.Api.Contracts;
using Charter.Api.Projects;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Deployments;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// What section 18's preview binding hands to section 27.7's card.
/// </summary>
/// <remarks>
/// The payload is written by the deployments module and read by the API projection, and the SPA's
/// union in <c>ClientApp/src/api/types.ts</c> is the contract both sides answer to. These tests read
/// the payload back through the API's own projection rather than asserting on a JSON string, because
/// a string assertion would pass happily while the two spelled <c>reachability</c> differently.
/// </remarks>
public class ArtifactPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static VerificationArtifact ReadyPreview(string url, PreviewReachability reachability)
    {
        var artifact = VerificationArtifact.Pending(
            Guid.CreateVersion7(),
            VerificationArtifactKind.HostedPreview,
            VerificationArtifactAudience.Requester,
            Now);

        artifact.MarkReady(
            url: url,
            expiresAt: Now.AddHours(8),
            payload: PreviewArtifactPublisher.Payload(url, reachability));

        return artifact;
    }

    [Theory]
    [InlineData(PreviewReachability.Reachable, ApiReachability.Reachable)]
    [InlineData(PreviewReachability.Unreachable, ApiReachability.Unreachable)]
    [InlineData(PreviewReachability.Unknown, ApiReachability.Unknown)]
    [InlineData(PreviewReachability.Checking, ApiReachability.Checking)]
    public void ThePayloadRoundTripsIntoTheHostedPreviewBodyTheCardRenders(
        PreviewReachability written,
        ApiReachability expected)
    {
        var artifact = ReadyPreview("https://quote-tool-pr-142.up.railway.app/quotes/new", written);

        var payload = Assert.IsType<HostedPreviewPayload>(ArtifactPayloads.For(artifact));

        Assert.Equal("https://quote-tool-pr-142.up.railway.app/quotes/new", payload.Url);
        Assert.Equal(expected, payload.Reachability);

        // The chip's short form is derived by the API from the URL it will actually open, so there is
        // no second, shorter copy that can disagree with it.
        Assert.False(string.IsNullOrWhiteSpace(payload.DisplayUrl));
    }

    [Fact]
    public async Task TheSerialisedBodyUsesTheSpellingsTheSpaUnionDeclares()
    {
        var artifact = ReadyPreview("https://preview.example.com", PreviewReachability.Reachable);

        var json = await ApiPayloads.RenderAsync(ArtifactPayloads.For(artifact));

        using var document = JsonDocument.Parse(json);

        Assert.Equal("https://preview.example.com", document.RootElement.GetProperty("url").GetString());
        Assert.Equal("reachable", document.RootElement.GetProperty("reachability").GetString());
        Assert.True(document.RootElement.TryGetProperty("displayUrl", out _));
    }

    [Fact]
    public void APayloadThatWillNotParseLeavesTheCardWithTheUrlItHas()
    {
        // A runner or provider writing malformed JSON should cost the card its detail, not turn the
        // request endpoint into a 500.
        var artifact = VerificationArtifact.Pending(
            Guid.CreateVersion7(),
            VerificationArtifactKind.HostedPreview,
            now: Now);

        artifact.MarkReady(url: "https://preview.example.com", payload: "{not json");

        var payload = Assert.IsType<HostedPreviewPayload>(ArtifactPayloads.For(artifact));

        Assert.Equal("https://preview.example.com", payload.Url);
        Assert.Equal(ApiReachability.Unknown, payload.Reachability);
    }

    [Fact]
    public void ThePreviewPayloadCarriesNothingButWhereThePreviewIsAndWhetherItAnswers()
    {
        // The "What to check" list is rendered from the approved spec verbatim (sections 11, 27.7).
        // A second copy of it inside the artifact would be a copy that can drift from the contract.
        var artifact = ReadyPreview("https://preview.example.com", PreviewReachability.Reachable);

        using var document = JsonDocument.Parse(artifact.Payload!);

        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(["url", "reachability"], names);
    }

    [Fact]
    public void TheApprovedAcceptanceCriteriaReachTheCardWordForWord()
    {
        var scenario = DeploymentScenario.Build(Now);
        var artifact = ReadyPreview("https://preview.example.com", PreviewReachability.Reachable);

        var aggregate = new RequestAggregate
        {
            Request = scenario.Request,
            Repo = scenario.Repo,
            Profile = RepoProjectProfile.For(scenario.Repo),
            Spec = scenario.Spec,
            Session = scenario.Session,
            Artifacts = [artifact],
            ChangeRequest = scenario.ChangeRequest,
        };

        var projected = RequestProjection.Spec(aggregate);

        Assert.NotNull(projected);
        Assert.Equal(
            DeploymentScenario.Criteria,
            projected.AcceptanceCriteria.Select(criterion => criterion.Text).ToList());

        // And the stored spec is untouched by anything the preview path did.
        Assert.Equal(JsonSerializer.Serialize(DeploymentScenario.Criteria), scenario.Spec.AcceptanceCriteria);
    }

    [Fact]
    public async Task ARequestersCardCarriesThePreviewAndNoCommitSha()
    {
        var scenario = DeploymentScenario.Build(Now);
        var artifact = ReadyPreview("https://quote-tool-pr-142.up.railway.app", PreviewReachability.Reachable);

        var aggregate = new RequestAggregate
        {
            Request = scenario.Request,
            Repo = scenario.Repo,
            Profile = RepoProjectProfile.For(scenario.Repo),
            Spec = scenario.Spec,
            Session = scenario.Session,
            Artifacts = [artifact],
        };

        var visibility = RequestVisibility.Resolve(
            MemberSnapshot.From(scenario.Member),
            RepoSnapshot.From(scenario.Repo, []),
            scenario.Request,
            scenario.Session);

        var body = await ApiPayloads.RenderAsync(RequestProjection.Detail(aggregate, visibility, Now));
        var keys = ApiPayloads.Keys(body);

        Assert.Contains("hosted_preview", body, StringComparison.Ordinal);
        Assert.Contains("quote-tool-pr-142.up.railway.app", body, StringComparison.Ordinal);

        // Section 27.7: `details` is omitted by the API, not hidden by CSS. Requesters never see a SHA.
        Assert.DoesNotContain("details", keys);
        Assert.DoesNotContain(DeploymentScenario.HeadSha, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstructionsAreWrittenForSomebodyWhoHasNeverSeenAChangeRequest()
    {
        Assert.DoesNotContain("pull request", PreviewArtifactPublisher.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deploy", PreviewArtifactPublisher.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("branch", PreviewArtifactPublisher.Instructions, StringComparison.OrdinalIgnoreCase);
    }
}
