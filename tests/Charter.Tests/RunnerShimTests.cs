using System.Text.Json;
using Charter.Adapters;

using Charter.Domain;
using Charter.Runners.Shim;

namespace Charter.Tests;

/// <summary>A workspace on disk that cleans itself up.</summary>
internal sealed class ShimWorkspace : IDisposable
{
    public ShimWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "charter-shim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Write(string relativePath, string content = "{}")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A test that leaves a file open should not fail on cleanup.
        }
    }
}

/// <summary>Replays canned agent output instead of starting a process.</summary>
internal sealed class FakeProcessRunner : IShimProcessRunner
{
    private readonly Dictionary<string, (int ExitCode, string[] Lines)> _responses = new(StringComparer.Ordinal);

    public List<ShimProcessRequest> Requests { get; } = [];

    /// <summary>True once a run observed its cancellation token being tripped.</summary>
    public bool WasCancelled { get; private set; }

    public void Respond(string fileName, int exitCode, params string[] lines)
        => _responses[fileName] = (exitCode, lines);

    public async Task<ShimProcessResult> RunAsync(
        ShimProcessRequest request,
        Func<string, CancellationToken, ValueTask>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (!_responses.TryGetValue(request.FileName, out var response))
        {
            return new ShimProcessResult(0);
        }

        foreach (var line in response.Lines)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Section 11: a cancelled run stops reading and the process dies with it.
                WasCancelled = true;
                break;
            }

            if (onStandardOutputLine is not null)
            {
                await onStandardOutputLine(line, cancellationToken);
            }
        }

        return new ShimProcessResult(response.ExitCode);
    }
}

/// <summary>Collects what the shim would have posted to the control plane.</summary>
internal sealed class FakeEventSink : IShimEventSink
{
    public List<ShimOutboundEvent> Events { get; } = [];

    public List<ShimResult> Results { get; } = [];

    public ValueTask PublishAsync(ShimOutboundEvent outboundEvent, CancellationToken cancellationToken)
    {
        Events.Add(outboundEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportResultAsync(ShimResult result, CancellationToken cancellationToken)
    {
        Results.Add(result);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSpecSource : IShimSpecSource
{
    public string Spec { get; set; } = "# Remember the last selected vertical";

    public ValueTask<string> LoadAsync(Uri specUrl, CancellationToken cancellationToken)
        => ValueTask.FromResult(Spec);
}

/// <summary>
/// Path scope, enforced in the runner rather than the UI (section 7.3).
/// </summary>
/// <remarks>
/// This is the guardrail a compromised session would most want to widen, so the tests are about what
/// is refused rather than about what is allowed.
/// </remarks>
public class RunnerPathScopeTests
{
    private static ShimPathScope Scope(string root) => ShimPathScope.Create(
        root,
        ["src/Features/**", "src/Web/Components/**"],
        ["src/Auth/**", "**/Migrations/**", ".github/**", "**/appsettings*.json"]);

    [Fact]
    public void AWriteInsideTheAllowListIsPermitted()
    {
        using var workspace = new ShimWorkspace();
        var scope = Scope(workspace.Root);

        Assert.True(scope.Permits("src/Features/Quotes/Wizard.cs"));
        Assert.True(scope.Permits("./src/Web/Components/Picker.tsx"));
    }

    [Fact]
    public void AWriteOutsideTheAllowListIsRefusedAndSaysWhatIsAllowed()
    {
        using var workspace = new ShimWorkspace();

        var decision = Scope(workspace.Root).Evaluate("src/Billing/Invoice.cs");

        Assert.False(decision.Allowed);
        Assert.Equal(PathScopeRefusal.NotAllowed, decision.Refusal);
        Assert.Contains("src/Features/**", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyBeatsAllow()
    {
        using var workspace = new ShimWorkspace();

        // Inside src/Features/** and also inside **/Migrations/**. A repository may only tighten.
        var decision = Scope(workspace.Root).Evaluate("src/Features/Migrations/0001_Add.cs");

        Assert.False(decision.Allowed);
        Assert.Equal(PathScopeRefusal.Denied, decision.Refusal);
        Assert.Equal("**/Migrations/**", decision.Pattern);
    }

    [Theory]
    [InlineData("../outside-the-repo.cs")]
    [InlineData("src/Features/../../../escape.cs")]
    [InlineData("/etc/passwd")]
    public void APathThatEscapesTheWorkspaceIsRefused(string path)
    {
        using var workspace = new ShimWorkspace();

        var decision = ShimPathScope.Create(workspace.Root).Evaluate(path);

        Assert.False(decision.Allowed);
        Assert.Equal(PathScopeRefusal.OutsideWorkspace, decision.Refusal);
    }

    [Theory]
    [InlineData(".charter/config.yml")]
    [InlineData(".charter/policies/migrations.yml")]
    [InlineData(".git/config")]
    public void TheSessionsOwnGuardrailsAreNeverWritableEvenWithAnEmptyDenyList(string path)
    {
        using var workspace = new ShimWorkspace();

        // No deny list at all, and an allow list covering everything: still refused.
        var decision = ShimPathScope.Create(workspace.Root, ["**"], []).Evaluate(path);

        Assert.False(decision.Allowed);
        Assert.Equal(PathScopeRefusal.AlwaysDenied, decision.Refusal);
    }

    [Fact]
    public void AnEmptyAllowListMeansTheWholeWorkspace()
    {
        using var workspace = new ShimWorkspace();

        Assert.True(ShimPathScope.Create(workspace.Root).Permits("anything/at/all.cs"));
    }

    [Theory]
    [InlineData("src/Features/**", "src/Features/A/B/C.cs", true)]
    [InlineData("src/Features/**", "src/Other/A.cs", false)]
    [InlineData("**/Migrations/**", "src/Data/Migrations/0001.cs", true)]
    [InlineData("**/appsettings*.json", "src/App/appsettings.Production.json", true)]
    [InlineData("**/appsettings*.json", "src/App/settings.json", false)]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/Deep/Program.cs", false)]
    public void TheGlobDialectIsTheOneCharterConfigWritesScopesIn(string pattern, string path, bool matches)
    {
        Assert.Equal(matches, ShimGlob.Matches(pattern, path));
    }
}

/// <summary>Section 16.2: lockfile-only installs, install scripts disabled by default.</summary>
public class RunnerDependencyInstallTests
{
    [Fact]
    public void NpmInstallsFromTheLockfileWithScriptsDisabled()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("package.json");
        workspace.Write("package-lock.json");

        var step = Assert.Single(ShimDependencyInstalls.Plan(workspace.Root).Steps);

        Assert.Equal("npm ci --ignore-scripts", step.Display);
        Assert.True(step.LockfileEnforced);
        Assert.True(step.InstallScriptsDisabled);
    }

    [Fact]
    public void DotnetRestoreRunsInLockedMode()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("packages.lock.json");

        var step = Assert.Single(ShimDependencyInstalls.Plan(workspace.Root).Steps);

        Assert.Equal("dotnet restore --locked-mode", step.Display);
        Assert.True(step.LockfileEnforced);
    }

    [Fact]
    public void InstallScriptsRunOnlyWhenTheRepositoryOptedInAndTheTranscriptSaysSo()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("package-lock.json");

        var plan = ShimDependencyInstalls.Plan(workspace.Root, allowInstallScripts: true);
        var step = Assert.Single(plan.Steps);

        Assert.Equal("npm ci", step.Display);
        Assert.False(step.InstallScriptsDisabled);
        Assert.Contains(plan.Warnings, warning => warning.Contains("opted in", StringComparison.Ordinal));
    }

    [Fact]
    public void APackageJsonWithNoLockfileWarnsRatherThanResolvingFreshVersions()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("package.json");

        var plan = ShimDependencyInstalls.Plan(workspace.Root);

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, warning => warning.Contains("lockfile", StringComparison.Ordinal));
    }

    [Fact]
    public void PnpmAndYarnAreFrozenToo()
    {
        using var pnpm = new ShimWorkspace();
        pnpm.Write("pnpm-lock.yaml");
        Assert.Equal(
            "pnpm install --frozen-lockfile --ignore-scripts",
            Assert.Single(ShimDependencyInstalls.Plan(pnpm.Root).Steps).Display);

        using var yarn = new ShimWorkspace();
        yarn.Write("yarn.lock");
        Assert.Equal(
            "yarn install --frozen-lockfile --ignore-scripts",
            Assert.Single(ShimDependencyInstalls.Plan(yarn.Root).Steps).Display);
    }
}

/// <summary>Section 32.1: a session never installs a language runtime.</summary>
public class RunnerToolchainTests
{
    [Fact]
    public void AProbedVersionSatisfiesACoarserRequirement()
    {
        var verdict = ShimToolchain.Verify(["linux", "dotnet:10"], ["linux", "dotnet:10.0.100"]);

        Assert.True(verdict.Satisfied);
        Assert.Empty(verdict.Missing);
    }

    [Fact]
    public void AMissingToolchainFailsFastWithAnActionableMessageAndNoInstall()
    {
        var verdict = ShimToolchain.Verify(
            ["linux", "dotnet:10"],
            ["linux", "node:22.11.0"],
            "ghcr.io/binn/charter-runner-node:1");

        Assert.False(verdict.Satisfied);
        Assert.Equal(["dotnet:10"], verdict.Missing);

        // Names what is missing, which image was used, and which image has it.
        Assert.Contains(".NET 10", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("charter-runner-node:1", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("charter-runner-dotnet", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("never installs a language runtime", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredRequirementOfNothingIsAlwaysSatisfied()
    {
        Assert.True(ShimToolchain.Verify([], []).Satisfied);
    }

    [Theory]
    [InlineData("dotnet", "10.0.100\n", "dotnet:10.0.100")]
    [InlineData("node", "v22.11.0\n", "node:22.11.0")]
    [InlineData("git", "", "git")]
    public void ProbeOutputBecomesACapabilityToken(string capability, string output, string expected)
    {
        Assert.Equal(expected, ShimCapabilityProbe.Format(capability, output));
    }
}

/// <summary>Turning agent output into events, and refusing writes on the way (sections 7.3, 12b).</summary>
public class RunnerEventTranslationTests
{
    private static AdapterDocument Adapter() => AdapterTestFiles.Load(AdapterTestFiles.ValidYaml);

    private static ShimEventTranslator Translator(string root, params string[] deny)
        => new(new AdapterEventClassifier(Adapter()), ShimPathScope.Create(root, [], deny));

    [Fact]
    public void AMappedLineBecomesAnEventWithAMonotonicIndex()
    {
        using var workspace = new ShimWorkspace();
        var translator = Translator(workspace.Root);

        var first = translator.Translate("""{"type":"assistant","text":"working on it"}""");
        var second = translator.Translate("""{"type":"tool_call","tool":"bash"}""");

        Assert.Equal(EventTypes.Message, first.Event!.Type);
        Assert.Equal(1, first.Event.Index);
        Assert.Equal(EventTypes.ToolUse, second.Event!.Type);
        Assert.Equal(2, second.Event.Index);
    }

    [Fact]
    public void AFileWriteInsideTheScopeCarriesThePathItTouched()
    {
        using var workspace = new ShimWorkspace();

        var outcome = Translator(workspace.Root)
            .Translate("""{"type":"tool_call","tool":"write","path":"src/Features/A.cs"}""");

        Assert.Null(outcome.Violation);
        Assert.Equal(EventTypes.FileWrite, outcome.Event!.Type);

        using var payload = JsonDocument.Parse(outcome.Event.Payload);
        Assert.Equal("src/Features/A.cs", payload.RootElement.GetProperty("paths")[0].GetString());
    }

    [Fact]
    public void AFileWriteOutsideTheScopeIsRefusedAndPublishesNothing()
    {
        using var workspace = new ShimWorkspace();

        var outcome = Translator(workspace.Root, "src/Auth/**")
            .Translate("""{"type":"tool_call","tool":"write","path":"src/Auth/Passwords.cs"}""");

        Assert.NotNull(outcome.Violation);
        Assert.Null(outcome.Event);
        Assert.Equal(PathScopeRefusal.Denied, outcome.Violation.Refusal);
    }

    [Fact]
    public void APathNestedInsideAToolCallIsStillFound()
    {
        // Claude Code puts it at message.content[0].input.file_path; others at path or file.
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"message":{"content":[{"name":"Edit","input":{"file_path":"src/Auth/Token.cs"}}]}}""");

        Assert.Equal(["src/Auth/Token.cs"], ShimFilePaths.Extract(node));
    }

    [Fact]
    public void AMalformedLineIsCountedRatherThanThrown()
    {
        using var workspace = new ShimWorkspace();
        var translator = Translator(workspace.Root);

        var outcome = translator.Translate("not json at all");

        Assert.Equal(AdapterLineKind.Malformed, outcome.Kind);
        Assert.Null(outcome.Event);
        Assert.Equal(1, translator.MalformedLines);
    }
}

/// <summary>The shim end to end, with the agent CLI and the control plane both faked.</summary>
public class RunnerShimSessionTests
{
    private static AdapterCatalog Catalog()
        => AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

    private static ShimRunRequest Request(string workspace, params string[] deny) => new()
    {
        SessionId = Guid.NewGuid(),
        WorkspaceRoot = workspace,
        AdapterId = "claude-code",
        Model = "anthropic/claude-opus-5",
        SpecUrl = new Uri("https://charter.example.com/spec"),
        DenyPaths = deny,
        RequiredCapabilities = ["linux"],
        ProbedCapabilities = ["linux", "dotnet:10.0.100"],

        // These tests are about the steps before the work is published: the toolchain check, the
        // lockfile installs, the event stream and the two guards that stop a run. The publishing step
        // is exercised against a real git repository with a real remote in RunnerPublishTests, which
        // is the only way to test it that is worth anything.
        Publish = false,
    };

    /// <summary>One line of Claude Code's <c>stream-json</c> output, as its adapter maps it.</summary>
    private static string ToolUse(string tool, string path)
        => "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\""
           + tool
           + "\",\"input\":{\"file_path\":\""
           + path
           + "\"}}]}}";

    [Fact]
    public async Task AHealthySessionInstallsFromLockfilesThenRunsTheAgentAndCompletes()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("package-lock.json");

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0, ToolUse("Write", "src/Features/A.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        // npm ci --ignore-scripts ran before the agent did.
        Assert.Equal(["npm", "claude"], processes.Requests.Select(request => request.FileName));
        Assert.Contains("--ignore-scripts", processes.Requests[0].Arguments);

        Assert.Contains(sink.Events, e => e.Type == EventTypes.SessionStarted);
        Assert.Contains(sink.Events, e => e.Type == EventTypes.FileWrite);
        Assert.Contains(sink.Events, e => e.Type == EventTypes.SessionEnded);
        Assert.Equal(ShimSessionState.Completed, Assert.Single(sink.Results).State);
    }

    [Fact]
    public async Task AnOutOfScopeWriteStopsTheSessionAndIsReported()
    {
        using var workspace = new ShimWorkspace();

        var processes = new FakeProcessRunner();
        processes.Respond(
            "claude",
            0,
            ToolUse("Write", "src/Features/Fine.cs"),
            ToolUse("Edit", "src/Auth/Passwords.cs"),
            ToolUse("Write", "src/Features/NeverReached.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(
            Request(workspace.Root, "src/Auth/**"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.ScopeViolation, result.State);
        Assert.Contains("src/Auth/Passwords.cs", result.Message, StringComparison.Ordinal);

        // The agent process was cancelled, so the third line never became an event.
        Assert.True(processes.WasCancelled);
        Assert.Single(sink.Events, e => e.Type == EventTypes.FileWrite);

        var error = Assert.Single(sink.Events, e => e.Type == EventTypes.Error);
        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("path_scope_violation", payload.RootElement.GetProperty("reason").GetString());

        // And the control plane is told, rather than being left to infer it from a lease timeout.
        Assert.Equal("failed", Assert.Single(sink.Results).Wire);
    }

    [Fact]
    public async Task AMissingToolchainStopsBeforeAnythingIsInstalled()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("package-lock.json");

        var processes = new FakeProcessRunner();
        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var request = Request(workspace.Root) with
        {
            RequiredCapabilities = ["macos", "xcode:16"],
            ProbedCapabilities = ["linux"],
            RunnerImage = "ghcr.io/binn/charter-runner-fullstack:1",
        };

        var result = await session.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.ToolchainMissing, result.State);

        // Nothing ran. Not the install, not the agent, and certainly not a package manager.
        Assert.Empty(processes.Requests);
        Assert.Contains("never installs a language runtime", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLockfileInstallStopsBeforeTheAgentStarts()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write("packages.lock.json");

        var processes = new FakeProcessRunner();
        processes.Respond("dotnet", 1);

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.InstallFailed, result.State);
        Assert.DoesNotContain(processes.Requests, request => request.FileName == "claude");
    }

    [Fact]
    public async Task CancellingTheSessionReportsCancelledRatherThanFailed()
    {
        using var workspace = new ShimWorkspace();

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0, ToolUse("Write", "src/A.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await session.RunAsync(Request(workspace.Root), cancelled.Token);

        Assert.Equal(ShimSessionState.Cancelled, result.State);

        // The terminal report still goes out: a killed session that says nothing leaves the control
        // plane waiting on a lease timeout and the requester on "Building this now".
        Assert.Equal("cancelled", Assert.Single(sink.Results).Wire);
    }

    /// <summary>An EF migration, shaped as the generator emits it.</summary>
    private static string Migration(string operation) => $$"""
        using Microsoft.EntityFrameworkCore.Migrations;

        public partial class Example : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                {{operation}}
            }
        }
        """;

    [Fact]
    public async Task ADestructiveMigrationHaltsTheSessionSoAnEngineerWritesItByHand()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write(
            "src/Data/Migrations/20260810_DropVertical.cs",
            Migration("""migrationBuilder.DropColumn(name: "vertical", table: "quotes");"""));

        var processes = new FakeProcessRunner();
        processes.Respond(
            "claude",
            0,
            ToolUse("Write", "src/Data/Migrations/20260810_DropVertical.cs"),
            ToolUse("Write", "src/Features/NeverReached.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.DestructiveMigration, result.State);
        Assert.Contains("by hand", result.Message, StringComparison.Ordinal);

        // The run stopped at the migration, so the next write never happened.
        Assert.True(processes.WasCancelled);
        Assert.Single(sink.Events, e => e.Type == EventTypes.FileWrite);

        var error = Assert.Single(sink.Events, e => e.Type == EventTypes.Error);
        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("destructive_migration", payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnAdditiveMigrationFlowsAndIsRecordedForTheSchemaChangeLabel()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write(
            "src/Data/Migrations/20260810_AddColumn.cs",
            Migration("""migrationBuilder.AddColumn<string>(name: "note", table: "quotes", nullable: true);"""));

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0, ToolUse("Write", "src/Data/Migrations/20260810_AddColumn.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        var check = Assert.Single(sink.Events, e => e.Type == EventTypes.CheckResult);
        using var payload = JsonDocument.Parse(check.Payload);
        Assert.Equal("additive", payload.RootElement.GetProperty("class").GetString());
        Assert.Equal("schema-change", payload.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task AnAmbiguousMigrationFlowsButIsFlaggedForReview()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write(
            "src/Data/Migrations/20260810_Rename.cs",
            Migration("""migrationBuilder.RenameColumn(name: "a", table: "quotes", newName: "b");"""));

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0, ToolUse("Write", "src/Data/Migrations/20260810_Rename.cs"));

        var sink = new FakeEventSink();
        var session = new ShimSession(Catalog(), processes, sink, new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        var check = Assert.Single(sink.Events, e => e.Type == EventTypes.CheckResult);
        using var payload = JsonDocument.Parse(check.Payload);
        Assert.Equal("ambiguous", payload.RootElement.GetProperty("class").GetString());
        Assert.Equal("RequiresReview", payload.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TheRepositorysMigrationPolicyIsWhatDecides()
    {
        using var workspace = new ShimWorkspace();
        workspace.Write(
            ".charter/policies/migrations.yml",
            """
            version: 1
            operations:
              rename_column: destructive
            """);
        workspace.Write(
            "src/Data/Migrations/20260810_Rename.cs",
            Migration("""migrationBuilder.RenameColumn(name: "a", table: "quotes", newName: "b");"""));

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0, ToolUse("Write", "src/Data/Migrations/20260810_Rename.cs"));

        var session = new ShimSession(Catalog(), processes, new FakeEventSink(), new FakeSpecSource());

        var result = await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        // The same migration that flowed above now halts, because this repository said so.
        Assert.Equal(ShimSessionState.DestructiveMigration, result.State);
    }

    [Theory]
    [InlineData("src/Data/Migrations/0001_Init.cs", true)]
    [InlineData("Migrations/0001_Init.cs", true)]
    [InlineData("src/Data/Migrations/0001_Init.Designer.cs", false)]
    [InlineData("src/Data/Migrations/CharterDbContextModelSnapshot.cs", false)]
    [InlineData("src/Features/Quotes.cs", false)]
    public void OnlyAHandEditableMigrationIsClassified(string path, bool isMigration)
    {
        Assert.Equal(isMigration, ShimMigrationGuard.IsGeneratedMigration(path));
    }

    [Fact]
    public async Task TheAgentProcessGetsTheAdapterInvocationAndTheSpecOnStandardInput()
    {
        using var workspace = new ShimWorkspace();

        var processes = new FakeProcessRunner();
        processes.Respond("claude", 0);

        var specs = new FakeSpecSource { Spec = "# The approved spec" };
        var session = new ShimSession(Catalog(), processes, new FakeEventSink(), specs);

        await session.RunAsync(Request(workspace.Root), TestContext.Current.CancellationToken);

        var agent = Assert.Single(processes.Requests);
        Assert.Equal("claude", agent.FileName);
        Assert.Contains("--print", agent.Arguments);
        Assert.Contains("stream-json", agent.Arguments);

        // The bare model name, not the provider-qualified one: that is what the CLI wants.
        Assert.Contains("claude-opus-5", agent.Arguments);
        Assert.Equal("# The approved spec", agent.StandardInput);
    }
}

/// <summary>The shim's command line, which is a contract with the shipped workflow.</summary>
public class RunnerShimCommandLineTests
{
    private static string[] WorkflowInvocation() =>
    [
        "run",
        "--session-id", Guid.NewGuid().ToString(),
        "--adapter", "claude-code",
        "--model", "anthropic/claude-opus-5",
        "--repo", "acme/spectra",
        "--base-branch", "main",
        "--base-commit", "a3f9c21",
        "--spec-url", "https://charter.example.com/spec",
        "--callback-url", "https://charter.example.com/cb",
        "--stream-events",
    ];

    [Fact]
    public void TheFlagsTheWorkflowPassesAllParse()
    {
        var problems = new List<string>();
        var command = ShimCommandLine.Parse(WorkflowInvocation(), problems);

        Assert.Empty(problems);
        Assert.Equal("claude-code", command.Adapter);
        Assert.Equal("acme/spectra", command.Repo);
        Assert.True(command.StreamEvents);
        Assert.False(command.AllowInstallScripts);
    }

    [Fact]
    public void EveryFlagTheWorkflowUsesIsOneTheShimUnderstands()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? workflow = null;

        while (directory is not null && workflow is null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "agent-session.yml");
            if (File.Exists(candidate))
            {
                workflow = File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        Assert.NotNull(workflow);

        // Only the shim's own invocation block: the workflow also runs curl, whose flags are none of
        // the shim's business.
        var invocation = workflow[workflow.IndexOf("charter-runner-shim run", StringComparison.Ordinal)..];
        invocation = invocation[..invocation.IndexOf("\n      -", StringComparison.Ordinal)];

        var flags = invocation
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("--", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(flags);

        var problems = new List<string>();
        _ = ShimCommandLine.Parse([.. flags.Select(flag => flag), "--session-id", Guid.NewGuid().ToString()], problems);

        Assert.DoesNotContain(problems, problem => problem.Contains("is not a flag", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingRequiredFlagIsReportedRatherThanDefaulted()
    {
        var problems = new List<string>();
        _ = ShimCommandLine.Parse(["run", "--adapter", "claude-code"], problems);

        Assert.Contains(problems, problem => problem.Contains("--session-id", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("--callback-url", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadablePathScopeDeniesEverythingRatherThanNothing()
    {
        var (allow, deny) = ShimPathScopeEnvironment.Read("{ this is not json");

        Assert.Empty(allow);
        Assert.Equal(["**"], deny);
    }

    [Fact]
    public void TheWorkflowsPathScopeJsonIsRead()
    {
        var (allow, deny) = ShimPathScopeEnvironment.Read(
            """{"allow":["src/Features/**"],"deny":["src/Auth/**"]}""");

        Assert.Equal(["src/Features/**"], allow);
        Assert.Equal(["src/Auth/**"], deny);
    }
}
