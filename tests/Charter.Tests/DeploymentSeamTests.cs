using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;

namespace Charter.Tests;

/// <summary>
/// The properties <see cref="IDeploymentProvider"/> has to hold for a second implementation to be a
/// configuration change rather than a rewrite.
/// </summary>
/// <remarks>
/// Change spec 001's implementation discipline asks for the second provider to be mapped onto the
/// seam on paper before the seam is called done. Two of those mappings are pinned here rather than
/// left in a document: a webhook-only platform, which is the shape a Render, Fly or Coolify
/// self-hoster runs today, and the vocabulary both ingestion paths have to share.
/// </remarks>
public class DeploymentSeamTests
{
    [Fact]
    public void EveryStateAProviderCanReportIsAStateTheGenericWebhookAccepts()
    {
        // Both ingestion paths write through one binder, and a provider's report is converted back
        // into the webhook's own vocabulary to get there. If these two ever disagreed, a preview
        // Railway reported would be refused by the code path that records it.
        foreach (var state in Enum.GetValues<DeploymentState>())
        {
            var report = new DeploymentReport("railway", state, "https://preview.example.com");
            var request = DeploymentIngestor.ToWebhookRequest(report);

            Assert.True(
                DeploymentBinder.TryParseState(request.State, out var parsed),
                $"The binder does not accept '{request.State}'.");

            Assert.Equal(state, parsed);
        }
    }

    [Fact]
    public void AWebhookOnlyPlatformIsAValidProviderShape()
    {
        // The paper mapping, made executable. A platform Charter can only be told about advertises
        // nothing, and every consumer checks a capability before assuming it has one.
        var capabilities = DeploymentProviderCapabilities.WebhookOnly;

        Assert.False(capabilities.Poll);
        Assert.False(capabilities.CommentParsing);
        Assert.False(capabilities.Teardown);
        Assert.False(capabilities.NativeExpiry);
    }

    [Fact]
    public void AnInstanceWithNoProviderIsAConfigurationRatherThanAFault()
    {
        var registry = new DeploymentProviderRegistry([]);

        Assert.Empty(registry.All);
        Assert.Null(registry.Configured);
        Assert.Null(registry.Find("railway"));
        Assert.Null(registry.Find(null));
    }

    [Fact]
    public void ProvidersAreFoundByIdWithoutCaringAboutCase()
    {
        var registry = new DeploymentProviderRegistry([new FakeProvider("render")]);

        Assert.NotNull(registry.Configured);
        Assert.Equal("render", registry.Configured.Id);
        Assert.NotNull(registry.Find("Render"));
        Assert.Null(registry.Find("railway"));
    }

    [Fact]
    public void AnObservationSaysWhichKindOfAnswerItIs()
    {
        Assert.Equal(DeploymentAvailability.NotYet, DeploymentObservation.NotYet().Availability);
        Assert.Equal(DeploymentAvailability.Blocked, DeploymentObservation.Blocked("invite them").Availability);
        Assert.Equal(DeploymentAvailability.Unsupported, DeploymentObservation.Unsupported("no").Availability);

        var reported = DeploymentObservation.Reported(new DeploymentReport("render", DeploymentState.Ready, "https://x"));

        Assert.Equal(DeploymentAvailability.Reported, reported.Availability);
        Assert.NotNull(reported.Report);

        // A blocked observation without a sentence would be worth nothing: the sentence is the value.
        Assert.Throws<ArgumentException>(() => DeploymentObservation.Blocked(" "));
    }

    [Fact]
    public void ATargetIsKeyedOnACommitRatherThanABranch()
    {
        // Change spec 001 §A.7: do not assume a change request is a branch. A Perforce shelved
        // changelist has none, and the seam still has to describe it.
        var target = new DeploymentTarget("northbeam/quote-tool", 142, "f00dcafe");

        Assert.Null(target.HeadBranch);
        Assert.Null(target.AuthorLogin);
        Assert.Null(target.HeadSeenAt);
        Assert.Equal("f00dcafe", target.HeadSha);
    }

    /// <summary>A provider that does nothing, for the registry's own behaviour.</summary>
    private sealed class FakeProvider : IDeploymentProvider
    {
        public FakeProvider(string id) => Id = id;

        public string Id { get; }

        public DeploymentProviderCapabilities Capabilities => DeploymentProviderCapabilities.WebhookOnly;

        public TimeSpan? PreviewLifetime => null;

        public Task<DeploymentObservation> ObserveAsync(
            DeploymentTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DeploymentObservation.Unsupported("this platform only calls the webhook"));

        public DeploymentObservation ReadComment(DeploymentComment comment, DeploymentTarget target)
            => DeploymentObservation.NotYet();

        public Task<DeploymentTeardownResult> TeardownAsync(
            DeploymentTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DeploymentTeardownResult.NothingToDo("this platform reclaims its own previews"));
    }
}
