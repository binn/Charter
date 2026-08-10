using Charter.Deployments;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// The fragile but universal fallback of section 18: reading a preview out of a change request
/// comment.
/// </summary>
/// <remarks>
/// The single most important property under test is negative. A comment that does not name a preview
/// must be "not yet" and never an error — the overwhelming majority of comments on a change request
/// are people talking to each other, and a parser that treated those as failures would turn the most
/// common event in a repository into a stream of alerts.
/// </remarks>
public class DeploymentCommentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static DeploymentTarget Target => new(
        "northbeam/quote-tool",
        142,
        "f00dcafe1234567890abcdef1234567890abcdef",
        "charter/remember-last-vertical");

    private static RailwayDeploymentProvider Railway()
        => new(
            new StubHttpClientFactory(new StubHttpMessageHandler()),
            new DeploymentOptions
            {
                Provider = DeploymentProviderKind.Railway,
                PreviewTtl = TimeSpan.FromHours(72),
                Railway = new RailwayOptions
                {
                    Token = Charter.Configuration.Secret.From("railway-token")!,
                    ProjectId = "proj_123",
                    BaseEnvironment = "staging",
                    ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
                },
            },
            new ModelFakeTimeProvider(Now),
            new RecordingLogger<RailwayDeploymentProvider>());

    [Fact]
    public void RailwaysReadyCommentYieldsThePreviewUrl()
    {
        var comment = new DeploymentComment(
            "railway[bot]",
            """
            🚅 The latest updates on your projects.

            | Service | Status | Preview |
            |---|---|---|
            | quote-tool | ✅ Deploy successful | [View](https://quote-tool-pr-142.up.railway.app) |

            [View logs](https://railway.com/project/proj_123/service/svc_1)
            """);

        var observation = Railway().ReadComment(comment, Target);

        Assert.Equal(DeploymentAvailability.Reported, observation.Availability);
        Assert.Equal(DeploymentState.Ready, observation.Report?.State);

        // The dashboard link sits right next to the preview one, and it is the last thing to put in
        // front of a requester who has no Railway account.
        Assert.Equal("https://quote-tool-pr-142.up.railway.app/", observation.Report?.Url);
    }

    [Fact]
    public void AnOrdinaryHumanCommentIsNotYetRatherThanAFailure()
    {
        var comment = new DeploymentComment(
            "ayesha",
            "Looks good to me, though I think the copy on the second step still says 'panel'.");

        var observation = Railway().ReadComment(comment, Target);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
        Assert.Null(observation.Report);
    }

    [Fact]
    public void ACommentFromSomebodyOtherThanRailwayIsIgnoredEvenWhenItLooksRight()
    {
        // Anybody who can comment on a change request can write a convincing line. An operator whose
        // own automation reports previews uses POST /api/deployments/{prSha}, which is an interface
        // rather than a guess at somebody's wording.
        var comment = new DeploymentComment(
            "passer-by",
            "Deploy successful: https://not-actually-the-preview.example.com");

        var observation = Railway().ReadComment(comment, Target);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public void ABotCommentAboutAFailureReportsTheFailureWithNoUrl()
    {
        var comment = new DeploymentComment("railway", "❌ Deploy failed for quote-tool. Build exited 1.");

        var observation = Railway().ReadComment(comment, Target);

        Assert.Equal(DeploymentAvailability.Reported, observation.Availability);
        Assert.Equal(DeploymentState.Failed, observation.Report?.State);
        Assert.Null(observation.Report?.Url);
    }

    [Fact]
    public void AFailureNextToAStaleLinkIsStillAFailure()
    {
        var comment = new DeploymentComment(
            "railway[bot]",
            "❌ Deploy failed. The previous build is at https://quote-tool-pr-142.up.railway.app");

        var observation = Railway().ReadComment(comment, Target);

        Assert.Equal(DeploymentState.Failed, observation.Report?.State);
        Assert.Null(observation.Report?.Url);
    }

    [Fact]
    public void TheBotSuffixIsMatchedLoosely()
    {
        Assert.True(PreviewCommentParser.IsFrom(new DeploymentComment("railway[bot]", ""), ["railway"]));
        Assert.True(PreviewCommentParser.IsFrom(new DeploymentComment("Railway", ""), ["railway"]));
        Assert.False(PreviewCommentParser.IsFrom(new DeploymentComment("railwayish", ""), ["railway"]));
        Assert.False(PreviewCommentParser.IsFrom(new DeploymentComment(null, ""), ["railway"]));
    }

    [Fact]
    public void UrlsAreReadOutOfMarkdownWithoutTheirPunctuation()
    {
        var urls = PreviewCommentParser.Urls(
            "Try [it](https://preview.example.com/quotes/new), then https://preview.example.com/done.");

        Assert.Equal(2, urls.Count);
        Assert.Equal("https://preview.example.com/quotes/new", urls[0].ToString());
        Assert.Equal("https://preview.example.com/done", urls[1].ToString());
    }

    [Fact]
    public void ARemovalWordBeatsADeploymentWord()
    {
        // A comment that says both is a teardown notice next to a link that no longer works.
        Assert.Equal(
            DeploymentState.Expired,
            PreviewCommentParser.State("The deployed preview environment was removed."));
    }

    [Fact]
    public void AUrlWithNoStateWordReadsAsReady()
    {
        // The shape of a bot comment that only ever appears once the environment is up.
        var observation = PreviewCommentParser.Read(
            new DeploymentComment("anything", "https://preview.example.com"),
            "generic");

        Assert.Equal(DeploymentState.Ready, observation.Report?.State);
    }

    [Fact]
    public void AnUnboundedCommentBodyIsNotHandedToTheRegularExpressionWhole()
    {
        var body = new string('a', PreviewCommentParser.MaxBodyLength + 4096) + " https://preview.example.com";

        // The tail past the cap is ignored rather than scanned, so the URL beyond it is not found.
        Assert.Empty(PreviewCommentParser.Urls(body));
    }

    [Fact]
    public void NothingAtAllIsStillNotYet()
    {
        Assert.Empty(PreviewCommentParser.Urls(null));
        Assert.Null(PreviewCommentParser.State(null));

        var observation = PreviewCommentParser.Read(new DeploymentComment("railway", string.Empty), "railway");

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }
}
