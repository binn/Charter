using System.Text.Json;
using Charter.Adapters;
using Charter.Domain;
using Charter.Runners.Shim;
using Charter.VersionControl;

namespace Charter.Tests;

/// <summary>
/// Section 8's named validation commands, run in the sandbox against a real repository.
/// </summary>
/// <remarks>
/// <para>
/// Real git and real commands in a temporary directory, for the reason <see cref="RunnerPublishTests"/>
/// gives: what is being tested is whether Charter can turn "the agent says it is done" into evidence,
/// and every interesting way that goes wrong — a command that is not there, a check that exits
/// non-zero, an image without the toolchain the checks need — is a fact about processes rather than
/// about a test double's idea of one.
/// </para>
/// <para>
/// The checks these tests declare run <c>git</c>, which is present wherever the publish tests already
/// run, exits zero and non-zero on demand, and needs nothing installed to prove the point.
/// </para>
/// </remarks>
public class RunnerChecksTests
{
    private static readonly Guid SessionId = Guid.Parse("0198f3a0-0000-7000-8000-0000000c4ec5");

    private static AdapterCatalog Catalog()
        => AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

    private static ShimSession Session(PublishProcessRunner processes, FakeEventSink sink)
        => new(Catalog(), processes, sink, new FakeSpecSource
        {
            Spec = "# Remember the last selected vertical\n\nThe wizard forgets the vertical.\n",
        });

    private static ShimRunRequest Request(GitWorkspace world, params string[] probed) => new()
    {
        SessionId = SessionId,
        WorkspaceRoot = world.Workspace,
        AdapterId = "claude-code",
        Model = "anthropic/claude-opus-5",
        SpecUrl = new Uri("https://charter.example.com/spec"),
        RequiredCapabilities = ["linux"],
        ProbedCapabilities = probed.Length == 0 ? ["linux", "git:2.39.0"] : probed,
        RunnerImage = "ghcr.io/binn/charter-runner-base:1",
        Requester = ShimCommitIdentity.TryCreate("Dana Okoro", "dana@example.test"),
    };

    private static string ToolUse(string tool, string path)
        => "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\""
           + tool
           + "\",\"input\":{\"file_path\":\""
           + path
           + "\"}}]}}";

    /// <summary>
    /// Commits a <c>.charter/config.yml</c> declaring the given checks.
    /// </summary>
    /// <remarks>
    /// Committed rather than left in the working tree, because that is what section 8 means by a
    /// committed guardrail — and because <see cref="ShimPathScope"/> refuses to let a session commit
    /// this file at all, which is the rule that stops a session widening its own guardrails.
    /// </remarks>
    private static void Declare(GitWorkspace world, params (string Name, string Run)[] checks)
    {
        var yaml = "version: 1\nbase_branch: main\nchecks:\n"
                   + string.Join(string.Empty, checks.Select(check => $"  - name: {check.Name}\n    run: \"{check.Run}\"\n"));

        world.Write(".charter/config.yml", yaml);
        world.Git("add", "--all");
        world.Git(
            "-c", "user.name=Base Author",
            "-c", "user.email=base@example.test",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", "Declare the repository's checks");
    }

    private static IReadOnlyList<JsonElement> Checks(FakeEventSink sink)
        => [.. sink.Events
            .Where(@event => @event.Type == EventTypes.CheckResult)
            .Select(@event => JsonDocument.Parse(@event.Payload).RootElement)];

    [Fact]
    public async Task TheRepositorysChecksRunAndTheirOutcomesReachTheTranscript()
    {
        using var world = GitWorkspace.Create();
        Declare(world, ("build", "git rev-parse HEAD"), ("test", "git status --short"));

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers the vertical\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        var checks = Checks(sink);
        Assert.Equal(2, checks.Count);

        Assert.Equal("build", checks[0].GetProperty("check").GetString());
        Assert.True(checks[0].GetProperty("passed").GetBoolean());
        Assert.Equal("git rev-parse HEAD", checks[0].GetProperty("command").GetString());
        Assert.Equal(0, checks[0].GetProperty("exit_code").GetInt32());
        Assert.Equal("passed", checks[0].GetProperty("status").GetString());

        Assert.Equal("test", checks[1].GetProperty("check").GetString());
        Assert.True(checks[1].GetProperty("passed").GetBoolean());

        // Section 14's recap counts them from the terminal event.
        var ended = Assert.Single(sink.Events, @event => @event.Type == EventTypes.SessionEnded);
        using var payload = JsonDocument.Parse(ended.Payload);
        Assert.Equal(2, payload.RootElement.GetProperty("checks_passed").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("checks_failed").GetInt32());
    }

    [Fact]
    public async Task ChecksRunAfterTheAgentAndBeforeTheWorkIsPublished()
    {
        using var world = GitWorkspace.Create();
        Declare(world, ("build", "git rev-parse HEAD"));

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();
        await Session(processes, sink).RunAsync(Request(world), TestContext.Current.CancellationToken);

        var order = sink.Events.Select(@event => @event.Type).ToList();

        // The order is the point: validating before the agent has finished would validate nothing,
        // and validating after the push would put the answer somewhere nobody is looking.
        Assert.True(order.IndexOf(EventTypes.SessionStarted) < order.IndexOf(EventTypes.CheckResult));
        Assert.True(order.IndexOf(EventTypes.CheckResult) < order.IndexOf(ChangeRequestEventTypes.BranchPushed));
    }

    [Fact]
    public async Task AFailingCheckIsReportedAndTheWorkIsStillPushed()
    {
        using var world = GitWorkspace.Create();

        // A revision that does not exist: git exits non-zero and says why.
        Declare(world, ("build", "git rev-parse --verify refs/heads/does-not-exist"));

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        // The decision, pinned: a check that fails is evidence about the change, not a reason to throw
        // the change away. Charter cannot merge it (section 7.4), so the reviewer decides — and they
        // can only decide about work they can see.
        Assert.Equal(ShimSessionState.Completed, result.State);

        var check = Assert.Single(Checks(sink));
        Assert.False(check.GetProperty("passed").GetBoolean());
        Assert.Equal("failed", check.GetProperty("status").GetString());
        Assert.NotEqual(0, check.GetProperty("exit_code").GetInt32());
        Assert.Contains("failed", check.GetProperty("summary").GetString()!, StringComparison.Ordinal);

        Assert.True(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));

        var ended = Assert.Single(sink.Events, @event => @event.Type == EventTypes.SessionEnded);
        using var payload = JsonDocument.Parse(ended.Payload);
        Assert.Equal(1, payload.RootElement.GetProperty("checks_failed").GetInt32());
    }

    [Fact]
    public async Task AnImageWithoutTheToolchainTheChecksNeedFailsFastRatherThanInstallingIt()
    {
        using var world = GitWorkspace.Create();
        Declare(world, ("build", "dotnet build"));

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// never written\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        // Probed, not assumed (section 32.2). This host has no .NET.
        var result = await Session(processes, sink).RunAsync(
            Request(world, "linux", "git:2.39.0"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.ToolchainMissing, result.State);

        // Section 32.1: nothing was installed, and nothing else was started either — not the agent,
        // and not the check that could never have worked.
        Assert.Empty(processes.Requests);

        var error = Assert.Single(
            sink.Events,
            @event => @event.Type == EventTypes.Error);

        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("toolchain_missing", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("checks", payload.RootElement.GetProperty("stage").GetString());

        var message = payload.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("the check 'build'", message, StringComparison.Ordinal);
        Assert.Contains(".NET", message, StringComparison.Ordinal);
        Assert.Contains("never installs a language runtime", message, StringComparison.Ordinal);
        Assert.Contains("runner_image", message, StringComparison.Ordinal);

        // And the work never happened, so there is nothing on the remote.
        Assert.False(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));
    }

    [Fact]
    public async Task ACheckThatCannotBeRunAsWrittenSaysSoInsteadOfPretendingItPassed()
    {
        using var world = GitWorkspace.Create();
        Declare(world, ("build", "git rev-parse HEAD && git status"));

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        var check = Assert.Single(Checks(sink));

        // "Did not run" and "failed" are different claims and section 14 keeps them apart: one is
        // evidence about the change, the other is the absence of it.
        Assert.Equal("notrun", check.GetProperty("status").GetString());
        Assert.False(check.GetProperty("passed").GetBoolean());

        var output = check.GetProperty("output").GetString()!;
        Assert.Contains("no shell", output, StringComparison.Ordinal);
        Assert.Contains(".charter/checks/", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionThatChangedNothingDoesNotSpendMinutesValidatingIt()
    {
        using var world = GitWorkspace.Create();
        Declare(world, ("build", "git rev-parse HEAD"));

        var processes = new PublishProcessRunner();
        processes.Agent("claude", null, "{\"type\":\"assistant\",\"message\":{\"content\":[]}}");

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);
        Assert.Empty(Checks(sink));
        Assert.Single(sink.Events, @event => @event.Type == ChangeRequestEventTypes.NoChanges);
    }

    [Fact]
    public async Task ADestructiveMigrationHaltsTheSessionAndPublishesNothing()
    {
        using var world = GitWorkspace.Create();

        const string migration = """
            using Microsoft.EntityFrameworkCore.Migrations;

            public partial class DropLegacyVertical : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(
                        name: "legacy_vertical",
                        table: "quotes");
                }

                protected override void Down(MigrationBuilder migrationBuilder)
                {
                }
            }
            """;

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Data/Migrations/20260811_DropLegacyVertical.cs", migration),
            ToolUse("Write", "src/Data/Migrations/20260811_DropLegacyVertical.cs"),

            // The agent would have carried on. It does not get to.
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        // Section 15: the session halts and an engineer authors the migration by hand.
        Assert.Equal(ShimSessionState.DestructiveMigration, result.State);
        Assert.Equal("failed", Assert.Single(sink.Results).Wire);

        var error = Assert.Single(
            sink.Events,
            @event => @event.Type == EventTypes.Error);

        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("destructive_migration", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal(
            "src/Data/Migrations/20260811_DropLegacyVertical.cs",
            payload.RootElement.GetProperty("path").GetString());
        Assert.Contains("by hand", payload.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // The classification is on the transcript, so the intent survives the halt.
        var classified = Assert.Single(Checks(sink));
        Assert.Equal("migration_classification", classified.GetProperty("check").GetString());
        Assert.Equal("destructive", classified.GetProperty("class").GetString());

        // Nothing was committed and nothing was pushed.
        Assert.False(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));
        Assert.Equal(world.BaseSha, world.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public void AChecksCommandLineIsSplitTheWayAShellWouldHaveSplitIt()
    {
        Assert.Equal(["dotnet", "build"], ShimChecks.Tokenize("dotnet build"));
        Assert.Equal(["dotnet", "test", "--filter", "Category=Fast"], ShimChecks.Tokenize("dotnet  test --filter Category=Fast"));
        Assert.Equal(["npm", "run", "lint:fix"], ShimChecks.Tokenize("npm run \"lint:fix\""));
        Assert.Equal(["make", "check this"], ShimChecks.Tokenize("make 'check this'"));
        Assert.Equal(["a", string.Empty], ShimChecks.Tokenize("a \"\""));
        Assert.Empty(ShimChecks.Tokenize("   "));
    }

    [Fact]
    public void OnlyToolchainsCharterActuallyProbesForAreGatedOn()
    {
        // Gating on a capability nothing probes for would fail every repository whose checks run
        // `make`, which is the false alarm that teaches an operator to ignore the real one.
        Assert.Null(ShimChecks.Resolve("build", "make all").Toolchain);
        Assert.Equal("dotnet", ShimChecks.Resolve("build", "dotnet build").Toolchain);
        Assert.Equal("node", ShimChecks.Resolve("test", "npm test").Toolchain);
        Assert.Equal("python", ShimChecks.Resolve("test", "pytest -q").Toolchain);

        var verdict = ShimChecks.VerifyToolchains(
            [ShimChecks.Resolve("build", "make all")],
            ["linux"],
            "ghcr.io/binn/charter-runner-base:1");

        Assert.True(verdict.Satisfied);
    }

    [Fact]
    public void AVersionedProbeSatisfiesACoarseRequirement()
    {
        // Section 32.2: a runner advertises `node:22.11.0` and a check needs `node`.
        var verdict = ShimChecks.VerifyToolchains(
            [ShimChecks.Resolve("test", "npm test")],
            ["linux", "node:22.11.0"]);

        Assert.True(verdict.Satisfied);
        Assert.Empty(verdict.Missing);
    }

    [Fact]
    public void ChecksAreReadFromTheRepositorysOwnConfig()
    {
        var yaml = """
            version: 1
            base_branch: main
            checks:
              - name: build
                run: "dotnet build"
              - name: test
                run: "dotnet test"
            """;

        var warnings = new List<string>();
        var checks = ShimChecks.Load("/workspace", warnings, _ => yaml);

        Assert.Equal(2, checks.Count);
        Assert.Equal("build", checks[0].Name);
        Assert.Equal("dotnet", checks[0].Command);
        Assert.Equal(["build"], checks[0].Arguments);
        Assert.True(checks[0].Runnable);
        Assert.Empty(warnings);

        // A repository with no config declares no checks, and that is not an error.
        Assert.Empty(ShimChecks.Load("/workspace", warnings, _ => null));
    }
}
