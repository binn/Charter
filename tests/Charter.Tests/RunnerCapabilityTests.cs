using System.Text.Json;
using Charter.Domain;
using Charter.Runners;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// A runner backend that records what it was asked to do instead of doing it.
/// </summary>
/// <remarks>
/// Shared by every test in this assembly that needs an execution plane without an execution plane.
/// It counts dispatches, which is the assertion the section 2.3 resumability tests hang on: after a
/// simulated container restart the count must still be one.
/// </remarks>
public sealed class RecordingRunner : IAgentRunner
{
    private readonly List<RunnerDispatch> _dispatches = [];
    private readonly List<RunnerCancellation> _cancellations = [];

    public RecordingRunner(
        RunnerKind kind = RunnerKind.GitHubActions,
        IReadOnlyList<string>? capabilities = null,
        bool online = true)
    {
        Kind = kind;
        Capabilities = RunnerCapability.ExpandAll(capabilities ?? ["linux", "dotnet:10", "node:22"]);
        Online = online;
    }

    public RunnerKind Kind { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public bool Online { get; set; }

    /// <summary>Set to refuse the next dispatch, the way a backend with no credentials would.</summary>
    public string? RefuseWith { get; set; }

    /// <summary>Set to throw from the next dispatch, the way a network failure would.</summary>
    public Exception? ThrowWith { get; set; }

    public string? ExternalReference { get; set; }

    public IReadOnlyList<RunnerDispatch> Dispatches => _dispatches;

    public IReadOnlyList<RunnerCancellation> Cancellations => _cancellations;

    public ValueTask<RunnerDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RunnerDescriptor(Kind, Kind.ToString(), Capabilities, Online));

    public Task<RunnerDispatchResult> DispatchAsync(
        RunnerDispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        if (ThrowWith is not null)
        {
            throw ThrowWith;
        }

        _dispatches.Add(dispatch);

        return Task.FromResult(RefuseWith is null
            ? RunnerDispatchResult.Ok(ExternalReference)
            : RunnerDispatchResult.Refused(RefuseWith));
    }

    public Task<RunnerCancelResult> CancelAsync(
        RunnerCancellation cancellation,
        CancellationToken cancellationToken = default)
    {
        _cancellations.Add(cancellation);
        return Task.FromResult(RunnerCancelResult.Confirmed);
    }
}

/// <summary>Records the <c>repository_dispatch</c> calls the Actions backend would make.</summary>
public sealed class RecordingGitHubDispatcher : IGitHubRepositoryDispatcher
{
    public List<(string Repo, string EventType, string Payload)> Dispatches { get; } = [];

    public List<(string Repo, long RunId)> Cancellations { get; } = [];

    public bool CancelResult { get; set; } = true;

    public Task RepositoryDispatchAsync(
        string repoFullName,
        string eventType,
        string clientPayloadJson,
        CancellationToken cancellationToken = default)
    {
        Dispatches.Add((repoFullName, eventType, clientPayloadJson));
        return Task.CompletedTask;
    }

    public Task<bool> CancelWorkflowRunAsync(
        string repoFullName,
        long runId,
        CancellationToken cancellationToken = default)
    {
        Cancellations.Add((repoFullName, runId));
        return Task.FromResult(CancelResult);
    }
}

/// <summary>
/// Capability tokens and the section 27.3 match.
/// </summary>
/// <remarks>
/// The property that matters is that the C# matcher and the Postgres <c>&lt;@</c> filter in
/// <see cref="Charter.Data.JobQueue"/> agree. They can only agree if an advertisement is expanded to
/// the coarser tokens it implies before either sees it, which is what these pin.
/// </remarks>
public class RunnerCapabilityTests
{
    [Fact]
    public void APreciseAdvertisementImpliesEveryCoarserForm()
    {
        // Section 32.2 probes produce dotnet:10.0.100; section 27.2 requires dotnet:10.
        Assert.Equal(
            ["dotnet", "dotnet:10", "dotnet:10.0", "dotnet:10.0.100"],
            RunnerCapability.Expand("dotnet:10.0.100"));
    }

    [Fact]
    public void ANonNumericVersionHasNoIntermediateForms()
    {
        Assert.Equal(["usb_device", "usb_device:stm32f4"], RunnerCapability.Expand("usb_device:stm32f4"));
    }

    [Fact]
    public void ABareCapabilityExpandsToItself()
    {
        Assert.Equal(["linux"], RunnerCapability.Expand("Linux"));
    }

    [Fact]
    public void AnExpandedAdvertisementSatisfiesACoarserRequirement()
    {
        var advertised = RunnerCapability.ExpandAll(["linux", "dotnet:10.0.100", "xcode:16.2"]);

        Assert.Empty(RunnerCapability.Missing([.. advertised], ["linux", "dotnet:10", "xcode:16"]));
    }

    [Fact]
    public void ACoarserAdvertisementDoesNotSatisfyAPreciseRequirement()
    {
        // Advertising "dotnet" says nothing about which .NET. Requiring 10 must not match it.
        var advertised = RunnerCapability.ExpandAll(["dotnet"]);

        Assert.Equal(["dotnet:10"], RunnerCapability.Missing([.. advertised], ["dotnet:10"]));
    }

    [Fact]
    public void CapabilitiesAreDescribedInTheWordsSection27Point3Uses()
    {
        Assert.Equal("macOS", RunnerCapability.Describe("macos"));
        Assert.Equal("Xcode 16", RunnerCapability.Describe("xcode:16"));
        Assert.Equal("macOS and Xcode 16", RunnerCapability.DescribeAll(["macos", "xcode:16"]));
        Assert.Equal("a, b and c", RunnerCapability.DescribeAll(["a", "b", "c"]));
    }
}

/// <summary>Routing a session to a backend, including the case where there is not one (section 27.3).</summary>
public class RunnerRoutingTests
{
    [Fact]
    public async Task ASessionRoutesToARunnerThatAdvertisesEverythingItNeeds()
    {
        var linux = new RecordingRunner(RunnerKind.GitHubActions, ["linux", "dotnet:10.0.100"]);
        var registry = new RunnerRegistry([linux]);

        var routing = await registry.RouteAsync(
            ["linux", "dotnet:10"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(routing.IsRoutable);
        Assert.Same(linux, routing.Runner);
        Assert.Null(routing.Explanation);
    }

    [Fact]
    public async Task ASessionWithNoEligibleRunnerQueuesWithAClearExplanation()
    {
        // The example from section 27.3, verbatim in shape:
        //   Runner advertises: ["linux", "docker", "dotnet:10", "node:22"]
        //   Session requires:  ["macos", "xcode:16"]
        //   -> queued: "No runner available with macOS and Xcode. Register one in Settings -> Runners."
        var registry = new RunnerRegistry(
            [new RecordingRunner(RunnerKind.GitHubActions, ["linux", "docker", "dotnet:10", "node:22"])]);

        var routing = await registry.RouteAsync(
            ["macos", "xcode:16"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(routing.IsRoutable);
        Assert.Null(routing.Runner);
        Assert.Equal(["macos", "xcode:16"], routing.Missing);
        Assert.Equal(
            "No runner available with macOS and Xcode 16. Register one in Settings → Runners.",
            routing.Explanation);
    }

    [Fact]
    public async Task NoRunnerAtAllSaysSoRatherThanNamingACapability()
    {
        var registry = new RunnerRegistry([]);

        var routing = await registry.RouteAsync(
            ["linux"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(routing.IsRoutable);
        Assert.Contains("CHARTER_RUNNER", routing.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfflineRunnerNeitherRoutesNorIsMistakenForAMissingCapability()
    {
        // "Your Mac mini is offline" is a far better message than "no runner has macOS" (section 33.3).
        var registry = new RunnerRegistry(
            [new RecordingRunner(RunnerKind.Agent, ["macos", "xcode:16.2"], online: false)]);

        var routing = await registry.RouteAsync(
            ["macos", "xcode:16"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(routing.IsRoutable);
        Assert.Contains("offline", routing.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdvertisedSetIsTheUnionOfEveryOnlineRunner()
    {
        var registry = new RunnerRegistry(
        [
            new RecordingRunner(RunnerKind.GitHubActions, ["linux"]),
            new RecordingRunner(RunnerKind.Agent, ["macos", "xcode:16.2"]),
            new RecordingRunner(RunnerKind.Docker, ["windows"], online: false),
        ]);

        var advertised = await registry.AdvertisedCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("linux", advertised);
        Assert.Contains("xcode:16", advertised);
        Assert.DoesNotContain("windows", advertised);
    }

    [Fact]
    public async Task TheSessionsOwnBackendWinsWhenSeveralCanRunIt()
    {
        var actions = new RecordingRunner(RunnerKind.GitHubActions, ["linux"]);
        var agent = new RecordingRunner(RunnerKind.Agent, ["linux"]);
        var registry = new RunnerRegistry([actions, agent]);

        var routing = await registry.RouteAsync(
            ["linux"],
            RunnerKind.Agent,
            TestContext.Current.CancellationToken);

        Assert.Same(agent, routing.Runner);
    }
}

/// <summary>
/// The <c>client_payload</c> contract with <c>.github/workflows/agent-session.yml</c>.
/// </summary>
/// <remarks>
/// The workflow reads every one of these by name. A rename here that is not made there produces a
/// session that dispatches successfully and then never runs, which is the worst failure shape
/// available: silent, remote, and only visible as a requester waiting forever.
/// </remarks>
public class RunnerGitHubActionsTests
{
    private static RunnerDispatch Dispatch() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "acme/spectra",
        "main",
        "a3f9c21",
        "claude-code",
        "anthropic/claude-opus-5",
        null,
        new Uri("https://charter.example.com/api/runners/sessions/11111111-2222-3333-4444-555555555555"),
        new Uri("https://charter.example.com/api/runners/sessions/11111111-2222-3333-4444-555555555555/spec"),
        new RunnerPathScope(["src/Features/**"], ["src/Auth/**"]),
        [],
        45,
        "dispatch:11111111-2222-3333-4444-555555555555");

    private static string WorkflowText()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "agent-session.yml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find .github/workflows/agent-session.yml.");
    }

    [Fact]
    public void EveryClientPayloadFieldTheWorkflowReadsIsSent()
    {
        var workflow = WorkflowText();
        var payload = GitHubActionsRunner.BuildPayload(Dispatch(), new GitHubActionsRunnerOptions());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var sent = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var field in new[]
                 {
                     "session_id", "callback_url", "repo", "base_branch", "base_commit_sha",
                     "adapter", "model", "runner_image", "timeout_minutes", "spec_url", "path_scope",
                 })
        {
            Assert.Contains($"client_payload.{field}", workflow, StringComparison.Ordinal);
            Assert.Contains(field, sent);
        }
    }

    [Fact]
    public void TheDispatchEventTypeIsTheOneTheWorkflowListensFor()
    {
        Assert.Contains(
            $"types: [{GitHubActionsRunnerOptions.EventType}]",
            WorkflowText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflowExchangesASessionSecretAtTheCredentialsEndpoint()
    {
        // Section 7.4: no credential travels in client_payload, which is readable by anyone with repo
        // read. The workflow's first step exchanges a bearer secret at {callback_url}/credentials.
        var workflow = WorkflowText();

        Assert.Contains("${CHARTER_CALLBACK_URL}/credentials", workflow, StringComparison.Ordinal);
        Assert.Contains("${CHARTER_CALLBACK_URL}/events", workflow, StringComparison.Ordinal);
        Assert.Contains("${CHARTER_CALLBACK_URL}/result", workflow, StringComparison.Ordinal);
        Assert.Contains(RunnerSessionTokens.RepositorySecretName, workflow, StringComparison.Ordinal);

        // And the fields the exchange responds with are the ones the workflow's jq reads.
        Assert.Contains("jq -r '.github_token'", workflow, StringComparison.Ordinal);
        Assert.Contains("jq -r '.event_token'", workflow, StringComparison.Ordinal);
        Assert.Contains("jq -r '.model_api_key // empty'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCallbackUrlHasNoTrailingSlashBecauseTheWorkflowAppendsToIt()
    {
        var dispatch = Dispatch() with
        {
            CallbackUrl = new Uri("https://charter.example.com/api/runners/sessions/abc/"),
        };

        var payload = GitHubActionsRunner.BuildPayload(dispatch, new GitHubActionsRunnerOptions());

        Assert.EndsWith("abc", payload.CallbackUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimeoutIsSentAsAStringBecauseFromJsonNeedsOne()
    {
        var payload = GitHubActionsRunner.BuildPayload(Dispatch(), new GitHubActionsRunnerOptions());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("timeout_minutes").ValueKind);
        Assert.Equal("45", payload.TimeoutMinutes);
    }

    [Fact]
    public void ADispatchWithNoImageFallsBackToTheFullstackImage()
    {
        var payload = GitHubActionsRunner.BuildPayload(Dispatch(), new GitHubActionsRunnerOptions());

        Assert.Equal(GitHubActionsRunnerOptions.DefaultRunnerImage, payload.RunnerImage);
        Assert.Contains(GitHubActionsRunnerOptions.DefaultRunnerImage, WorkflowText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchingPostsARepositoryDispatchAndNothingElse()
    {
        var github = new RecordingGitHubDispatcher();
        var runner = new GitHubActionsRunner(
            github,
            new GitHubActionsRunnerOptions(),
            NullLogger<GitHubActionsRunner>.Instance);

        var result = await runner.DispatchAsync(Dispatch(), TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);

        // repository_dispatch answers 204 with no body, so there is no run id to record yet.
        Assert.Null(result.ExternalReference);

        var (repo, eventType, json) = Assert.Single(github.Dispatches);
        Assert.Equal("acme/spectra", repo);
        Assert.Equal(GitHubActionsRunnerOptions.EventType, eventType);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellingParsesTheRunUrlTheWorkflowReported()
    {
        var github = new RecordingGitHubDispatcher();
        var runner = new GitHubActionsRunner(
            github,
            new GitHubActionsRunnerOptions(),
            NullLogger<GitHubActionsRunner>.Instance);

        var result = await runner.CancelAsync(
            new RunnerCancellation(Guid.NewGuid(), "https://github.com/acme/spectra/actions/runs/98765", "cancel"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Stopped);
        Assert.Equal(("acme/spectra", 98765L), Assert.Single(github.Cancellations));
    }

    [Fact]
    public async Task CancellingBeforeTheRunReportedItselfIsNotAFailure()
    {
        var github = new RecordingGitHubDispatcher();
        var runner = new GitHubActionsRunner(
            github,
            new GitHubActionsRunnerOptions(),
            NullLogger<GitHubActionsRunner>.Instance);

        var result = await runner.CancelAsync(
            new RunnerCancellation(Guid.NewGuid(), null, "cancel"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Stopped);
        Assert.NotNull(result.Explanation);
        Assert.Empty(github.Cancellations);
    }
}

/// <summary>The two bearer secrets the sandbox uses (sections 7.4, 33.5).</summary>
public class RunnerSessionTokenTests
{
    private static RunnerSessionTokens Tokens() => new("a-test-signing-key-of-sufficient-length-000000");

    [Fact]
    public void TheSessionSecretIsStablePerRepositoryAndDiffersBetweenThem()
    {
        var tokens = Tokens();

        Assert.Equal(tokens.SessionSecretFor("acme/spectra"), tokens.SessionSecretFor("ACME/Spectra"));
        Assert.NotEqual(tokens.SessionSecretFor("acme/spectra"), tokens.SessionSecretFor("acme/other"));
        Assert.True(tokens.ValidateSessionSecret("acme/spectra", tokens.SessionSecretFor("acme/spectra")));
        Assert.False(tokens.ValidateSessionSecret("acme/other", tokens.SessionSecretFor("acme/spectra")));
    }

    [Fact]
    public void AnEventTokenIsBoundToOneSessionAndExpires()
    {
        var tokens = Tokens();
        var session = Guid.NewGuid();
        var token = tokens.IssueEventToken(session);

        Assert.True(tokens.ValidateEventToken(session, token));
        Assert.False(tokens.ValidateEventToken(Guid.NewGuid(), token));
        Assert.False(tokens.ValidateEventToken(
            session,
            token,
            DateTimeOffset.UtcNow + RunnerSessionTokens.EventTokenLifetime + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ATamperedEventTokenIsRefused()
    {
        var tokens = Tokens();
        var session = Guid.NewGuid();
        var token = tokens.IssueEventToken(session);
        var parts = token.Split('.');

        // Push the expiry out by a year without re-signing.
        var forged = $"{parts[0]}.{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}.{parts[2]}";

        Assert.False(tokens.ValidateEventToken(session, forged));
        Assert.False(tokens.ValidateEventToken(session, null));
        Assert.False(tokens.ValidateEventToken(session, "nonsense"));
    }

    [Fact]
    public void ADifferentInstanceKeyValidatesNothing()
    {
        var mine = Tokens();
        var theirs = new RunnerSessionTokens("a-completely-different-signing-key-11111111111");
        var session = Guid.NewGuid();

        Assert.False(theirs.ValidateEventToken(session, mine.IssueEventToken(session)));
        Assert.False(theirs.ValidateSessionSecret("acme/spectra", mine.SessionSecretFor("acme/spectra")));
    }
}
