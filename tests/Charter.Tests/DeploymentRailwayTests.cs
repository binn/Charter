using System.Globalization;
using System.Net;
using Charter.Deployments;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// The one Phase 1 implementation of <see cref="IDeploymentProvider"/>, against a stubbed Railway.
/// </summary>
/// <remarks>
/// No test here makes a network call. Every response is canned, which is the only way to assert what
/// happens on the paths that matter most — a preview that never arrives, and a schema that moved.
/// </remarks>
public class DeploymentRailwayTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static DeploymentOptions Options(int ttlHours = 72) => new()
    {
        Provider = DeploymentProviderKind.Railway,
        PreviewTtl = TimeSpan.FromHours(ttlHours),
        Railway = new RailwayOptions
        {
            Token = Charter.Configuration.Secret.From("railway-token")!,
            ProjectId = "proj_123",
            BaseEnvironment = "staging",
            ApiUrl = new Uri(RailwayOptions.DefaultApiUrl),
        },
    };

    private static RailwayDeploymentProvider Provider(
        StubHttpMessageHandler handler,
        DeploymentOptions? options = null,
        DateTimeOffset? now = null)
        => new(
            new StubHttpClientFactory(handler),
            options ?? Options(),
            new ModelFakeTimeProvider(now ?? Now),
            new RecordingLogger<RailwayDeploymentProvider>());

    private static DeploymentTarget Target(DateTimeOffset? seen = null)
        => new(
            "northbeam/quote-tool",
            142,
            "f00dcafe1234567890abcdef1234567890abcdef",
            "charter/remember-last-vertical",
            "outside-contributor",
            seen);

    private static string Environments(params string[] nodes)
        => "{\"data\":{\"environments\":{\"edges\":[" + string.Join(",", nodes) + "]}}}";

    private static string PreviewEnvironment(
        int number = 142,
        string id = "env_pr142",
        string branch = "charter/remember-last-vertical")
        => "{\"node\":{\"id\":\"" + id + "\",\"name\":\"pr-" + number.ToString(CultureInfo.InvariantCulture) +
           "\",\"isEphemeral\":true,\"meta\":{\"branch\":\"" + branch + "\",\"prNumber\":" +
           number.ToString(CultureInfo.InvariantCulture) + "}}}";

    private static string BaseEnvironment()
        => "{\"node\":{\"id\":\"env_staging\",\"name\":\"staging\",\"isEphemeral\":false,\"meta\":{}}}";

    private static string Deployments(string status, string? staticUrl, string commit)
    {
        var url = staticUrl is null ? "null" : "\"" + staticUrl + "\"";

        return "{\"data\":{\"deployments\":{\"edges\":[{\"node\":{\"id\":\"dep_1\",\"status\":\"" + status +
               "\",\"staticUrl\":" + url + ",\"meta\":{\"commitHash\":\"" + commit + "\"}}}]}}}";
    }

    [Fact]
    public void TheCapabilitiesSayWhatRailwayCanActuallyDo()
    {
        var provider = Provider(new StubHttpMessageHandler());

        Assert.Equal("railway", provider.Id);
        Assert.True(provider.Capabilities.Poll);
        Assert.True(provider.Capabilities.CommentParsing);
        Assert.True(provider.Capabilities.Teardown);

        // Railway keeps a PR environment for as long as the change request is open, so the countdown
        // of section 27.7 comes from Charter's configured lifetime rather than from the platform.
        Assert.False(provider.Capabilities.NativeExpiry);
        Assert.Equal(TimeSpan.FromHours(72), provider.PreviewLifetime);
    }

    [Fact]
    public async Task ASuccessfulDeploymentOfTheHeadCommitIsReportedWithItsUrlAndAnExpiry()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(BaseEnvironment(), PreviewEnvironment()))
            .EnqueueJson(Deployments("SUCCESS", "quote-tool-pr-142.up.railway.app", Target().HeadSha));

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.Reported, observation.Availability);
        Assert.NotNull(observation.Report);
        Assert.Equal(DeploymentState.Ready, observation.Report.State);

        // A bare host from staticUrl would resolve against Charter's own origin in a browser.
        Assert.Equal("https://quote-tool-pr-142.up.railway.app/", observation.Report.Url);
        Assert.Equal(Now.AddHours(72), observation.Report.ExpiresAt);
    }

    [Fact]
    public async Task ADeploymentOfADifferentCommitIsNotThisChangeRequestsPreview()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(PreviewEnvironment()))
            .EnqueueJson(Deployments("SUCCESS", "old-build.up.railway.app", "0000000000000000000000000000000000000000"));

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
        Assert.Null(observation.Report);
    }

    [Theory]
    [InlineData("BUILDING", DeploymentState.Building)]
    [InlineData("DEPLOYING", DeploymentState.Building)]
    [InlineData("QUEUED", DeploymentState.Pending)]
    [InlineData("FAILED", DeploymentState.Failed)]
    [InlineData("CRASHED", DeploymentState.Failed)]
    [InlineData("REMOVED", DeploymentState.Expired)]
    [InlineData("SKIPPED", DeploymentState.Cancelled)]
    public async Task RailwaysVocabularyMapsOntoChartersCommonDenominator(string status, DeploymentState expected)
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(PreviewEnvironment()))
            .EnqueueJson(Deployments(status, "quote-tool-pr-142.up.railway.app", Target().HeadSha));

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.Reported, observation.Availability);
        Assert.Equal(expected, observation.Report?.State);
    }

    [Fact]
    public async Task ASuccessWithNoUrlIsNotReadyYet()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(PreviewEnvironment()))
            .EnqueueJson(Deployments("SUCCESS", null, Target().HeadSha));

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public async Task NoEnvironmentWithinTheGracePeriodIsSimplyNotYet()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(Environments(BaseEnvironment()));

        var observation = await Provider(handler).ObserveAsync(
            Target(seen: Now.AddMinutes(-2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public async Task AnAuthorOutsideTheWorkspaceIsSurfacedRatherThanLeftAsSilence()
    {
        // Section 18: Railway will not deploy a change request branch from an account outside the
        // workspace unless it has been invited. Nothing reports an error — the environment is simply
        // never created — so a preview that never arrives is the only signal there is.
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(
                BaseEnvironment(),
                PreviewEnvironment(number: 7, id: "env_pr7", branch: "someone-elses-branch")));

        var observation = await Provider(handler).ObserveAsync(
            Target(seen: Now.AddHours(-1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.Blocked, observation.Availability);
        Assert.NotNull(observation.Explanation);
        Assert.Contains("invited", observation.Explanation, StringComparison.Ordinal);
        Assert.Contains("outside-contributor", observation.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoEphemeralEnvironmentsAtAllReadsAsPreviewsBeingSwitchedOff()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(Environments(BaseEnvironment()));

        var observation = await Provider(handler).ObserveAsync(
            Target(seen: Now.AddHours(-1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.Blocked, observation.Availability);
        Assert.Contains("switched off", observation.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTokenIsNotYetRatherThanAnExplosion()
    {
        var handler = new StubHttpMessageHandler().EnqueueError(HttpStatusCode.Unauthorized, "{}");

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public async Task AGraphQlErrorIsNotYetRatherThanAnExplosion()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"errors":[{"message":"Not Authorized"}]}""");

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public async Task AShapeRailwayHasNeverSentDegradesToNotYet()
    {
        // The schema is Railway's and changes on Railway's schedule. A field that moved must not take
        // the preview binding with it.
        var handler = new StubHttpMessageHandler().EnqueueJson("""{"data":{"environments":"gone"}}""");

        var observation = await Provider(handler).ObserveAsync(Target(), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAvailability.NotYet, observation.Availability);
    }

    [Fact]
    public async Task TearingDownDeletesTheEphemeralEnvironment()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(Environments(BaseEnvironment(), PreviewEnvironment()))
            .EnqueueJson("""{"data":{"environmentDelete":true}}""");

        var result = await Provider(handler).TeardownAsync(Target(), TestContext.Current.CancellationToken);

        Assert.True(result.TornDown);
        Assert.Contains("env_pr142", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("environmentDelete", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TearingDownSomethingRailwayAlreadyReclaimedIsNotAFailure()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(Environments(BaseEnvironment()));

        var result = await Provider(handler).TeardownAsync(Target(), TestContext.Current.CancellationToken);

        Assert.False(result.TornDown);
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public void TheProviderRefusesToExistWithoutItsConfiguration()
    {
        var options = new DeploymentOptions
        {
            Provider = DeploymentProviderKind.Railway,
            PreviewTtl = TimeSpan.FromHours(72),
        };

        Assert.Throws<ArgumentException>(() => Provider(new StubHttpMessageHandler(), options));
    }
}
