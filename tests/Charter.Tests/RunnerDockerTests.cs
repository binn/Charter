using Charter.Configuration;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Shim;
using Charter.VersionControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>A Docker daemon that answers without one being installed.</summary>
internal sealed class FakeDockerEngine : IDockerEngine
{
    private readonly List<(DockerContainerSpec Spec, string Id)> _containers = [];

    public bool Reachable { get; set; } = true;

    public List<string> Killed { get; } = [];

    public Exception? RefuseWith { get; set; }

    public IReadOnlyList<DockerContainerSpec> Started => [.. _containers.Select(entry => entry.Spec)];

    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Reachable);

    public Task<IReadOnlyList<DockerContainerSummary>> ListByLabelAsync(
        string label,
        string value,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DockerContainerSummary>>(
        [
            .. _containers
                .Where(entry => entry.Spec.Labels.TryGetValue(label, out var actual)
                                && string.Equals(actual, value, StringComparison.Ordinal))
                .Select(entry => new DockerContainerSummary(entry.Id, "running")),
        ]);

    public Task<string> RunAsync(DockerContainerSpec spec, CancellationToken cancellationToken = default)
    {
        if (RefuseWith is not null)
        {
            throw RefuseWith;
        }

        var id = $"container-{_containers.Count + 1}";
        _containers.Add((spec, id));

        return Task.FromResult(id);
    }

    public Task<bool> KillAsync(string containerId, CancellationToken cancellationToken = default)
    {
        Killed.Add(containerId);
        return Task.FromResult(true);
    }
}

/// <summary>Mints credentials without a GitHub App behind it.</summary>
internal sealed class StubCredentialBroker : IRunnerCredentialBroker
{
    public Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RunnerCredentials("ghs_short_ttl", "sk-scoped"));
}

/// <summary>
/// The Compose self-host backend of section 2.2.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The decision these tests pin.</strong> <c>CHARTER_RUNNER=docker</c> used to validate,
/// register nothing, and queue every session forever with no error anywhere — the one unacceptable
/// outcome. The fix is the backend itself rather than a refusal at startup, for three reasons.
/// Section 2.2 lists <c>DockerRunner</c> as a real backend with a real use case, and refusing it would
/// mean shipping a documented option that does not exist. The seam it plugs into already exists and
/// the shim it drives is the same one the workflow runs, so the backend is a container create away
/// rather than a subsystem. And a startup refusal would have to live in configuration parsing, where
/// it would tell a Compose self-hoster to go and use GitHub Actions — which is precisely the backend
/// that cannot reach their machine.
/// </para>
/// <para>
/// What is <em>not</em> silent either way: an instance configured for Docker with no socket registers
/// a runner that describes itself as offline, so section 27.3's routing explains the session rather
/// than leaving it in a queue nobody is watching.
/// </para>
/// </remarks>
public class RunnerDockerTests
{
    private static readonly Guid SessionId = Guid.Parse("0198f3a0-0000-7000-8000-00000000d0c5");

    private static RunnerDispatch Dispatch(string? image = null) => new(
        SessionId,
        "acme/spectra",
        "main",
        "a3f9c21",
        "claude-code",
        "anthropic/claude-opus-5",
        image,
        new Uri("https://charter.example.com/api/runners/sessions/x"),
        new Uri("https://charter.example.com/api/runners/sessions/x/spec"),
        new RunnerPathScope(["src/Features/**"], ["src/Auth/**"]),
        ["linux", "dotnet:10"],
        45,
        "dispatch:one")
    {
        Branch = ChangeRequestPublisher.BranchFor(SessionId),
        Requester = new RunnerRequester("Dana Okoro", "dana@example.test"),
    };

    private static DockerRunner Runner(FakeDockerEngine engine, DockerRunnerOptions? options = null)
        => new(engine, options ?? new DockerRunnerOptions(), NullLogger<DockerRunner>.Instance);

    [Fact]
    public void CharterRunnerDockerRegistersARunner()
    {
        var parsed = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_RUNNER", "docker")));
        Assert.Empty(parsed.Problems);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterRunners(parsed.Config!);

        using var provider = services.BuildServiceProvider();
        var runner = Assert.Single(provider.GetServices<IAgentRunner>());

        // The failure this replaces: a configuration that validated and registered nothing.
        Assert.Equal(RunnerKind.Docker, runner.Kind);
    }

    [Fact]
    public async Task TheContainerRunsTheSameShimTheShippedWorkflowRuns()
    {
        var engine = new FakeDockerEngine();
        var result = await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);

        var spec = Assert.Single(engine.Started);
        Assert.Equal("charter-runner-shim", spec.Cmd[0]);

        // Section 3.1: one shim, one documented flag surface, three backends invoking it.
        var problems = new List<string>();
        var command = ShimCommandLine.Parse([.. spec.Cmd.Skip(1)], problems);

        Assert.Empty(problems);
        Assert.Equal("claude-code", command.Adapter);
        Assert.Equal("acme/spectra", command.Repo);
        Assert.Equal(ChangeRequestPublisher.BranchFor(SessionId), command.Branch);
        Assert.True(command.StreamEvents);
    }

    [Fact]
    public async Task TheContainerCarriesTheRequesterSoTheCommitNamesAPerson()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var problems = new List<string>();
        var command = ShimCommandLine.Parse(
            [.. Assert.Single(engine.Started).Cmd.Skip(1)],
            problems);

        Assert.Empty(problems);

        var identity = ShimCommitIdentity.TryCreate(command.RequesterName, command.RequesterEmail);
        Assert.Equal("Dana Okoro <dana@example.test>", identity?.Format());
    }

    [Fact]
    public async Task ThePathScopeReachesTheSandboxWhereItIsEnforced()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var variable = Assert.Single(
            Assert.Single(engine.Started).Env,
            entry => entry.StartsWith($"{ShimPathScopeEnvironment.Variable}=", StringComparison.Ordinal));

        var (allow, deny) = ShimPathScopeEnvironment.Read(
            variable[(ShimPathScopeEnvironment.Variable.Length + 1)..]);

        // Section 7.3: sent on the dispatch, enforced in the runner. Both halves or neither.
        Assert.Equal(["src/Features/**"], allow);
        Assert.Equal(["src/Auth/**"], deny);
    }

    [Fact]
    public async Task TheContainerHoldsExactlyTheThreePerSessionSecretsAndNothingElse()
    {
        var engine = new FakeDockerEngine();

        var runner = new DockerRunner(
            engine,
            new DockerRunnerOptions(),
            NullLogger<DockerRunner>.Instance,
            new RunnerSessionTokens(ConfigTestEnvironment.SecretKey),
            new StubCredentialBroker());

        await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var names = Assert.Single(engine.Started).Env
            .Select(entry => entry.Split('=', 2)[0])
            .Order(StringComparer.Ordinal);

        // Sections 7.4 and 33.5: a callback token for one session, a version-control token for one
        // repository, a scoped model credential. Nothing long-lived, and nothing from the control
        // plane's own environment.
        Assert.Equal(
            ["CHARTER_EVENT_TOKEN", "CHARTER_MODEL_API_KEY", "CHARTER_PATH_SCOPE", "CHARTER_SESSION_ID", "GITHUB_TOKEN"],
            names);
    }

    [Fact]
    public void SecretsNeverPrintThemselves()
    {
        // The default record printer would put a live credential into any interpolated string that
        // touched one — including a log line written by somebody who did not know they held it.
        var secrets = new DockerSessionSecrets("event-token", "ghs_live", "sk-live");

        Assert.DoesNotContain("ghs_live", secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", $"{secrets}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADispatchWithNoBrokerCarriesNoCredentialAtAll()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        foreach (var entry in Assert.Single(engine.Started).Env)
        {
            Assert.DoesNotContain("TOKEN", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("API_KEY", entry, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheContainerIsToldWhereToCloneFromBecauseItStartsEmpty()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var problems = new List<string>();
        var command = ShimCommandLine.Parse([.. Assert.Single(engine.Started).Cmd.Skip(1)], problems);

        Assert.Empty(problems);
        Assert.Equal("https://github.com/acme/spectra.git", command.CloneUrl);
        Assert.Equal("a3f9c21", command.BaseCommit);
    }

    [Fact]
    public async Task CachesAreScopedToOneRepositoryAndNeverShared()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var binds = Assert.Single(engine.Started).HostConfig.Binds;

        // Section 32.3: a cache shared across repositories is a cross-repo contamination path, not an
        // optimisation opportunity.
        Assert.NotEmpty(binds);
        Assert.All(binds, bind => Assert.Contains("acme-spectra", bind, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchingTheSameSessionTwiceStartsOneContainer()
    {
        var engine = new FakeDockerEngine();
        var runner = Runner(engine);

        var first = await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);
        var second = await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        // Section 2.3: the control plane can restart between starting a container and recording that
        // it did. The daemon's own labels are the memory, because this class has none.
        Assert.Single(engine.Started);
        Assert.Equal(first.ExternalReference, second.ExternalReference);
        Assert.True(second.Accepted);
    }

    [Fact]
    public async Task CancellingKillsTheContainerEvenWithNoExternalReferenceToGoOn()
    {
        var engine = new FakeDockerEngine();
        var runner = Runner(engine);

        await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        // A restarted control plane has no reference to offer; the session label is what makes
        // section 11's cancel button actually kill the runner.
        var result = await runner.CancelAsync(
            new RunnerCancellation(SessionId, null, "The requester cancelled."),
            TestContext.Current.CancellationToken);

        Assert.True(result.Stopped);
        Assert.Equal(["container-1"], engine.Killed);
    }

    [Fact]
    public async Task CancellingWillNotKillAContainerThatIsNotThisSessions()
    {
        // The same defect as the run URL, in the backend that reads the reference as a container id.
        // The reference is folded from the session's events, and `session_started` arrives from the
        // execution plane, so a sandbox that posts `{"run_url":"<any container id>"}` used to have
        // Charter `docker kill` it on the operator's own host — and report the cancel confirmed while
        // its own session went on running. The session label is written by this runner at dispatch and
        // is the one statement about ownership the sandbox never touched (sections 11, 16).
        var engine = new FakeDockerEngine();
        var runner = Runner(engine);

        await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var result = await runner.CancelAsync(
            new RunnerCancellation(SessionId, "container-belonging-to-something-else", "The requester cancelled."),
            TestContext.Current.CancellationToken);

        Assert.Empty(engine.Killed);
        Assert.False(result.Stopped);
        Assert.Contains("not one of this session's containers", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingKillsTheContainerTheReferenceNamesWhenItIsThisSessions()
    {
        var engine = new FakeDockerEngine();
        var runner = Runner(engine);

        var dispatched = await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        var result = await runner.CancelAsync(
            new RunnerCancellation(SessionId, dispatched.ExternalReference, "The requester cancelled."),
            TestContext.Current.CancellationToken);

        Assert.True(result.Stopped);
        Assert.Equal(["container-1"], engine.Killed);
    }

    [Fact]
    public async Task CancellingASessionWithNoContainerIsNotAFailure()
    {
        var result = await Runner(new FakeDockerEngine()).CancelAsync(
            new RunnerCancellation(SessionId, null, "The requester cancelled."),
            TestContext.Current.CancellationToken);

        Assert.False(result.Stopped);
        Assert.Contains("settled in Charter", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADaemonThatIsNotAnsweringIsOfflineRatherThanAbsent()
    {
        var engine = new FakeDockerEngine { Reachable = false };
        var descriptor = await Runner(engine).DescribeAsync(TestContext.Current.CancellationToken);

        Assert.False(descriptor.Online);

        // Section 27.3: an offline runner still explains itself. "Docker is not answering" beats a
        // session that queues forever with nothing said about it.
        var registry = new RunnerRegistry([Runner(engine)]);
        var routing = await registry.RouteAsync(["linux"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(routing.IsRoutable);
        Assert.Contains("offline", routing.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Docker", routing.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADaemonThatRefusesQueuesTheSessionWithAnExplanationRatherThanFailingIt()
    {
        var engine = new FakeDockerEngine
        {
            RefuseWith = new InvalidOperationException("no such image: ghcr.io/binn/charter-runner-fullstack:1"),
        };

        var result = await Runner(engine).DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("no such image", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstanceConfiguredForDockerWithNoSocketSaysWhatToUseInstead()
    {
        var engine = new UnreachableDockerEngine("/var/run/nowhere.sock");

        Assert.False(await engine.PingAsync(TestContext.Current.CancellationToken));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync(
                new DockerContainerSpec { Image = "x" },
                TestContext.Current.CancellationToken));

        // Section 4.1: fail loud, and name what to do about it.
        Assert.Contains("/var/run/nowhere.sock", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("CHARTER_RUNNER", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("agent", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acme/spectra", "acme-spectra")]
    [InlineData("ACME/Spectra.Web", "acme-spectra-web")]
    public void TheCacheScopeIsAVolumeSafeFormOfTheRepositoryName(string repo, string expected)
        => Assert.Equal(expected, DockerRunner.CacheScope(repo));

    [Fact]
    public void TheSocketPathHonoursTheEcosystemsOwnVariableBeforeItsDefault()
    {
        Assert.Equal("/tmp/charter.sock", DockerRunnerEnvironment.SocketPath("unix:///tmp/charter.sock"));
        Assert.Equal("/tmp/charter.sock", DockerRunnerEnvironment.SocketPath("/tmp/charter.sock"));
    }

    [Fact]
    public async Task TheImageComesFromTheRepositoryConfigurationWhenItNamesOne()
    {
        var engine = new FakeDockerEngine();
        await Runner(engine).DispatchAsync(
            Dispatch("ghcr.io/acme/charter-runner-embedded:3"),
            TestContext.Current.CancellationToken);

        var spec = Assert.Single(engine.Started);

        // Section 32.1: the toolchain comes from the image, and a session never installs one — so the
        // image the repository declared is the one that has to run.
        Assert.Equal("ghcr.io/acme/charter-runner-embedded:3", spec.Image);
        Assert.Contains("ghcr.io/acme/charter-runner-embedded:3", spec.Cmd);
    }
}
