using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Charter.Adapters;
using Charter.Domain;
using Charter.Runners.Shim;
using Charter.VersionControl;

namespace Charter.Tests;

/// <summary>
/// A real git repository, with a real bare remote, in a temporary directory.
/// </summary>
/// <remarks>
/// Not a mock, on purpose. What is being tested here is whether Charter can turn an agent's work into
/// a commit on a branch that a change request can be opened from, and every interesting way that goes
/// wrong — a detached HEAD, an empty index, a path git reports differently from the way the agent
/// announced it, a remote that is not there — is a fact about git rather than about a test double.
/// </remarks>
internal sealed class GitWorkspace : IDisposable
{
    private GitWorkspace(string root, string workspace, string remote, string baseSha)
    {
        Root = root;
        Workspace = workspace;
        Remote = remote;
        BaseSha = baseSha;
    }

    public string Root { get; }

    /// <summary>The checkout the session runs in.</summary>
    public string Workspace { get; }

    /// <summary>A bare repository standing in for the origin the session pushes to.</summary>
    public string Remote { get; }

    /// <summary>Where the session branched from (section 17).</summary>
    public string BaseSha { get; }

    public static GitWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "charter-git-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var remote = Path.Combine(root, "remote.git");

        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(remote);

        Run(root, "init", "--bare", "-b", "main", remote);
        Run(root, "init", "-b", "main", workspace);

        File.WriteAllText(Path.Combine(workspace, "README.md"), "# Spectra\n");
        Directory.CreateDirectory(Path.Combine(workspace, "src", "Features"));
        File.WriteAllText(Path.Combine(workspace, "src", "Features", "Quotes.cs"), "// quotes\n");

        Run(workspace, "add", "--all");
        Commit(workspace, "Add the project");
        Run(workspace, "remote", "add", "origin", remote);
        Run(workspace, "push", "origin", "refs/heads/main:refs/heads/main");

        return new GitWorkspace(root, workspace, remote, Run(workspace, "rev-parse", "HEAD").Trim());
    }

    /// <summary>Writes a file into the checkout, creating directories as needed.</summary>
    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Runs git in the checkout and returns standard output.</summary>
    public string Git(params string[] arguments) => Run(Workspace, arguments);

    /// <summary>Runs git in the bare remote — what the world sees after a push.</summary>
    public string OnRemote(params string[] arguments) => Run(Remote, arguments);

    /// <summary>True when the remote has the branch the session was meant to publish.</summary>
    public bool RemoteHasBranch(string branch)
        => Run(Remote, "for-each-ref", "--format=%(refname:short)", "refs/heads/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), branch, StringComparison.Ordinal));

    public void Dispose()
    {
        try
        {
            // git marks pack files read-only, which Directory.Delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Cleanup is not the assertion.
        }
    }

    private static readonly string EmptyConfig = CreateEmptyConfig();

    private static string CreateEmptyConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-git-tests", "empty.gitconfig");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        return path;
    }

    private static void Commit(string directory, string message)
        => Run(
            directory,
            "-c",
            "user.name=Base Author",
            "-c",
            "user.email=base@example.test",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "--no-verify",
            "-m",
            message);

    private static string Run(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // An empty global config, so a developer's own git settings cannot decide whether the setup
        // commits in these tests succeed.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfig;
        info.Environment["GIT_CONFIG_SYSTEM"] = EmptyConfig;

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("git is required to run the publish tests.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {error}");
    }
}

/// <summary>
/// Scripts the agent CLI while letting git through to the real thing.
/// </summary>
/// <remarks>
/// The shim drives every child process through one seam, and this splits it: the agent is canned
/// output plus whatever it "wrote" into the workspace, and git is git. That split is what makes these
/// tests about the publishing step rather than about a fake's idea of what git would have said.
/// </remarks>
internal sealed class PublishProcessRunner : IShimProcessRunner
{
    private readonly ProcessShimRunner _git = new();
    private readonly Dictionary<string, (int ExitCode, Action? Work, string[] Lines)> _agents =
        new(StringComparer.Ordinal);

    /// <summary>Every non-git process the shim started, in order.</summary>
    public List<ShimProcessRequest> Requests { get; } = [];

    public void Agent(string fileName, Action? work, params string[] lines)
        => _agents[fileName] = (0, work, lines);

    public async Task<ShimProcessResult> RunAsync(
        ShimProcessRequest request,
        Func<string, CancellationToken, ValueTask>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.FileName, "git", StringComparison.Ordinal))
        {
            return await _git.RunAsync(request, onStandardOutputLine, cancellationToken);
        }

        Requests.Add(request);

        if (!_agents.TryGetValue(request.FileName, out var script))
        {
            return new ShimProcessResult(0);
        }

        script.Work?.Invoke();

        foreach (var line in script.Lines)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (onStandardOutputLine is not null)
            {
                await onStandardOutputLine(line, cancellationToken);
            }
        }

        return new ShimProcessResult(script.ExitCode);
    }
}

/// <summary>
/// The step that gives <see cref="ChangeRequestPublisher"/> an input: commit, push, say so.
/// </summary>
/// <remarks>
/// Without this the Phase 1 loop has a hole nothing else can fill — <c>branch_pushed</c> is read by
/// the publisher and written by nobody, so every clean session records <c>NoChangesNeeded</c> and the
/// requester never sees anything (sections 2.2, 6).
/// </remarks>
public class RunnerPublishTests
{
    private static AdapterCatalog Catalog()
        => AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

    private static readonly Guid SessionId = Guid.Parse("0198f3a0-0000-7000-8000-00000000f00d");

    private static ShimRunRequest Request(GitWorkspace world, params string[] deny) => new()
    {
        SessionId = SessionId,
        WorkspaceRoot = world.Workspace,
        AdapterId = "claude-code",
        Model = "anthropic/claude-opus-5",
        SpecUrl = new Uri("https://charter.example.com/spec"),
        DenyPaths = deny,
        RequiredCapabilities = ["linux"],
        ProbedCapabilities = ["linux"],
        Requester = ShimCommitIdentity.TryCreate("Dana Okoro", "dana@example.test"),
    };

    private static string ToolUse(string tool, string path)
        => "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\""
           + tool
           + "\",\"input\":{\"file_path\":\""
           + path
           + "\"}}]}}";

    private static ShimSession Session(PublishProcessRunner processes, FakeEventSink sink)
        => new(Catalog(), processes, sink, new FakeSpecSource
        {
            Spec = "# Remember the last selected vertical\n\n"
                   + "The quote wizard forgets which vertical was picked when the page reloads. It "
                   + "should remember the last selection for the rest of the session.\n",
        });

    [Fact]
    public async Task ASessionWithChangesCommitsPushesAndReportsTheBranch()
    {
        using var world = GitWorkspace.Create();

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

        var branch = ChangeRequestPublisher.BranchFor(SessionId);
        Assert.True(world.RemoteHasBranch(branch));

        // The exact shape ChangeRequestPublisher reads: a branch and a revision.
        var pushed = Assert.Single(sink.Events, e => e.Type == ChangeRequestEventTypes.BranchPushed);
        using var payload = JsonDocument.Parse(pushed.Payload);

        Assert.Equal(branch, payload.RootElement.GetProperty("branch").GetString());

        var revision = payload.RootElement.GetProperty("revision").GetString();
        Assert.Equal(world.OnRemote("rev-parse", $"refs/heads/{branch}").Trim(), revision);
        Assert.NotEqual(world.BaseSha, revision);

        // And the file the agent wrote is in it.
        Assert.Contains(
            "src/Features/Wizard.cs",
            world.OnRemote("show", "--name-only", "--format=", $"refs/heads/{branch}"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEventTheShimEmitsIsTheOneTheChangeRequestPublisherReads()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();
        await Session(processes, sink).RunAsync(Request(world), TestContext.Current.CancellationToken);

        var pushed = Assert.Single(sink.Events, e => e.Type == ChangeRequestEventTypes.BranchPushed);

        // The publisher reads `branch`, and `revision` (or `sha`) — both as JSON strings, from an
        // object. Anything else and it falls back to the convention and finds nothing to publish.
        using var payload = JsonDocument.Parse(pushed.Payload);

        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("revision").ValueKind);
        Assert.Equal(40, payload.RootElement.GetProperty("revision").GetString()!.Length);
    }

    [Fact]
    public async Task TheEventTheShimEmitsIsEnoughToOpenAChangeRequest()
    {
        await using var control = await ChangeRequestWorld.CreateAsync();
        if (control is null)
        {
            return;
        }

        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world) with { SessionId = control.Session.Id },
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        // Recorded the way the runner callback records it, with nothing added or reshaped.
        var pushed = Assert.Single(sink.Events, e => e.Type == ChangeRequestEventTypes.BranchPushed);

        await new Charter.Orchestration.SessionJournal(control.Db).AppendAsync(
            control.Session.Id,
            pushed.Type,
            pushed.Payload,
            $"runner:{pushed.Index}",
            cancellationToken: TestContext.Current.CancellationToken);

        var revision = world.OnRemote("rev-parse", $"refs/heads/{control.Branch}").Trim();
        control.Provider.Comparisons[("basesha", revision)] =
            new RevisionComparison(1, 0, ["src/Features/Wizard.cs"]);

        control.Db.ChangeTracker.Clear();

        // The step that has never had an input, given one.
        var publication = await control.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, publication.Outcome);
        Assert.Equal(control.Branch, publication.ChangeRequest!.HeadBranch);
        Assert.Equal(revision, publication.ChangeRequest.HeadSha);
        Assert.Equal(SessionStatus.PrOpen, await control.StatusAsync());
    }

    [Fact]
    public async Task ASessionThatChangedNothingSaysSoRatherThanFailingAPush()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent("claude", null, "{\"type\":\"assistant\",\"message\":{\"content\":[]}}");

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        // Section 6: NoChangesNeeded is a real outcome. The session completed; it simply had nothing
        // to publish, and that is not a failed push.
        Assert.Equal(ShimSessionState.Completed, result.State);
        Assert.Equal("completed", Assert.Single(sink.Results).Wire);

        var none = Assert.Single(sink.Events, e => e.Type == ChangeRequestEventTypes.NoChanges);
        using var payload = JsonDocument.Parse(none.Payload);
        Assert.Equal("nothing_to_commit", payload.RootElement.GetProperty("reason").GetString());

        Assert.DoesNotContain(sink.Events, e => e.Type == ChangeRequestEventTypes.BranchPushed);
        Assert.DoesNotContain(sink.Events, e => e.Type == EventTypes.Error);

        // Nothing was pushed, and nothing was committed either.
        Assert.False(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));
        Assert.Equal(world.BaseSha, world.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task AnOutOfScopeWriteTheAgentNeverAnnouncedStillFailsTheSession()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () =>
            {
                world.Write("src/Features/Wizard.cs", "// in scope\n");

                // Never announced. The streaming check cannot see this one, which is exactly why
                // section 7.3's enforcement has to happen on what is about to be committed.
                world.Write("src/Auth/Passwords.cs", "// nowhere near the scope\n");
            },
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world, "src/Auth/**"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.ScopeViolation, result.State);
        Assert.Contains("src/Auth/Passwords.cs", result.Message, StringComparison.Ordinal);

        // Loudly: an error on the transcript, a failed terminal report, and nothing committed or
        // pushed — including the part of the work that was in scope.
        var error = Assert.Single(sink.Events, e => e.Type == EventTypes.Error);
        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("path_scope_violation", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("commit", payload.RootElement.GetProperty("stage").GetString());

        Assert.Equal("failed", Assert.Single(sink.Results).Wire);
        Assert.False(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));
        Assert.Equal(world.BaseSha, world.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task APushThatCannotHappenFailsTheSessionRatherThanLookingLikeNoChanges()
    {
        using var world = GitWorkspace.Create();
        world.Git("remote", "remove", "origin");

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        // The distinction that matters: a broken credential must never read to a requester as an
        // agent that decided there was nothing to do.
        Assert.Equal(ShimSessionState.PublishFailed, result.State);
        Assert.Equal("failed", Assert.Single(sink.Results).Wire);

        var error = Assert.Single(sink.Events, e => e.Type == EventTypes.Error);
        using var payload = JsonDocument.Parse(error.Payload);
        Assert.Equal("publish_failed", payload.RootElement.GetProperty("reason").GetString());

        Assert.DoesNotContain(sink.Events, e => e.Type == ChangeRequestEventTypes.NoChanges);
        Assert.DoesNotContain(sink.Events, e => e.Type == ChangeRequestEventTypes.BranchPushed);

        // The work is committed locally, so an engineer who fetches the runner has it.
        Assert.NotEqual(world.BaseSha, world.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task TheCommitIsAttributedToTheRequesterAndToNothingElse()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        await Session(processes, new FakeEventSink()).RunAsync(
            Request(world),
            TestContext.Current.CancellationToken);

        var branch = ChangeRequestPublisher.BranchFor(SessionId);
        var identity = world.OnRemote("log", "-1", "--format=%an|%ae|%cn|%ce", branch).Trim();

        // Section 24: the person who asked, on both sides. No bot, no machine account, no model.
        Assert.Equal("Dana Okoro|dana@example.test|Dana Okoro|dana@example.test", identity);

        var message = world.OnRemote("log", "-1", "--format=%B", branch);

        Assert.StartsWith("Remember the last selected vertical", message, StringComparison.Ordinal);
        Assert.Contains("forgets which vertical", message, StringComparison.Ordinal);

        foreach (var banned in new[] { "co-authored", "generated with", "assisted", "🤖", "charter" })
        {
            Assert.DoesNotContain(banned, message, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing about the tooling anywhere else in the commit object either.
        var raw = world.OnRemote("cat-file", "commit", branch);
        Assert.DoesNotContain("Co-authored-by", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADispatchWithNoRequesterCommitsWithTheCheckoutsOwnIdentity()
    {
        using var world = GitWorkspace.Create();
        world.Git("config", "user.name", "Repo Owner");
        world.Git("config", "user.email", "owner@example.test");

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        var result = await Session(processes, new FakeEventSink()).RunAsync(
            Request(world) with { Requester = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(ShimSessionState.Completed, result.State);

        // A session whose requester has since been removed still builds, and still does not invent an
        // author for the history.
        var branch = ChangeRequestPublisher.BranchFor(SessionId);
        Assert.Equal(
            "Repo Owner|owner@example.test",
            world.OnRemote("log", "-1", "--format=%an|%ae", branch).Trim());
    }

    [Fact]
    public async Task TheBranchTheShimPublishesOnIsTheOneTheControlPlaneLooksFor()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => world.Write("src/Features/Wizard.cs", "// remembers\n"),
            ToolUse("Write", "src/Features/Wizard.cs"));

        // No branch on the dispatch at all: both sides compute the same convention unaided, which is
        // what lets a runner that has never spoken to the control plane land where it will look.
        var request = Request(world);
        Assert.Null(request.Branch);
        Assert.Equal(ChangeRequestPublisher.BranchFor(SessionId), request.BranchOrConvention);

        await Session(processes, new FakeEventSink()).RunAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(world.RemoteHasBranch(ChangeRequestPublisher.BranchFor(SessionId)));
    }

    [Fact]
    public async Task ADeletionCountsAsAChangeAndIsScopeCheckedLikeAWrite()
    {
        using var world = GitWorkspace.Create();

        var processes = new PublishProcessRunner();
        processes.Agent(
            "claude",
            () => File.Delete(Path.Combine(world.Workspace, "src", "Features", "Quotes.cs")),
            "{\"type\":\"assistant\",\"message\":{\"content\":[]}}");

        var sink = new FakeEventSink();

        var result = await Session(processes, sink).RunAsync(
            Request(world, "src/Features/**"),
            TestContext.Current.CancellationToken);

        // Deny beats everything, and a delete is a change to a denied path just as a write is.
        Assert.Equal(ShimSessionState.ScopeViolation, result.State);
        Assert.Contains("src/Features/Quotes.cs", result.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The checkout step: a workspace to work in, for the one backend whose container starts empty.
/// </summary>
public class RunnerCheckoutTests
{
    [Fact]
    public async Task AWorkspaceThatIsAlreadyACheckoutIsLeftAlone()
    {
        using var world = GitWorkspace.Create();

        var problem = await new ShimCheckout(new PublishProcessRunner()).EnsureAsync(
            world.Workspace,
            "https://example.invalid/never-reached.git",
            world.BaseSha,
            null,
            TestContext.Current.CancellationToken);

        // A workflow job and a Charter Agent both hand the shim a checkout. Cloning over one would be
        // a way to lose whatever the backend had already prepared.
        Assert.Null(problem);
        Assert.Equal(world.BaseSha, world.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task AnEmptyWorkspaceIsClonedAtTheBaseCommit()
    {
        using var source = GitWorkspace.Create();
        var target = Path.Combine(source.Root, "container-workspace");

        var problem = await new ShimCheckout(new PublishProcessRunner()).EnsureAsync(
            target,
            source.Remote,
            source.BaseSha,
            null,
            TestContext.Current.CancellationToken);

        Assert.Null(problem);

        // Section 17: at the exact revision the session was planned against, because staleness is
        // computed from it and a session that started somewhere else makes that comparison a lie.
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
    }

    [Fact]
    public async Task AnEmptyWorkspaceWithNowhereToCloneFromFailsTheSessionWithASentence()
    {
        var target = Path.Combine(Path.GetTempPath(), "charter-git-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var problem = await new ShimCheckout(new PublishProcessRunner()).EnsureAsync(
                target,
                null,
                null,
                null,
                TestContext.Current.CancellationToken);

            Assert.NotNull(problem);
            Assert.Contains("not a git checkout", problem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://github.com/acme/spectra.git", "ghs_abc", "https://x-access-token:ghs_abc@github.com/acme/spectra.git")]
    [InlineData("https://github.com/acme/spectra.git", null, "https://github.com/acme/spectra.git")]
    [InlineData("git@github.com:acme/spectra.git", "ghs_abc", "git@github.com:acme/spectra.git")]
    public void AShortTtlTokenGoesIntoAnHttpsRemoteAndNowhereElse(string url, string? token, string expected)
        => Assert.Equal(expected, ShimCheckout.Authenticate(url, token));

    [Fact]
    public void ACredentialNeverReachesAnEventPayload()
    {
        // Git puts the remote URL in its error messages, and those become transcript events that
        // anyone with repository read access can see (section 7.4).
        var message = ShimCheckout.Redact(
            "fatal: could not read from https://x-access-token:ghs_live@github.com/acme/spectra.git",
            "ghs_live");

        Assert.DoesNotContain("ghs_live", message, StringComparison.Ordinal);
        Assert.Contains("***", message, StringComparison.Ordinal);
    }
}

/// <summary>The message of a commit Charter generates (sections 16, 24).</summary>
public class RunnerCommitMessageTests
{
    [Fact]
    public void TheSpecificationsTitleIsTheSubjectAndItsOpeningParagraphTheBody()
    {
        var message = ShimCommitMessage.Build(
            "# Exclude tax from the invoice total\n\nThe total currently includes tax, which confuses "
            + "customers reading the invoice.\n\n## Acceptance criteria\n\n- The total excludes tax\n");

        var lines = message.Split('\n');

        Assert.Equal("Exclude tax from the invoice total", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Contains("confuses customers", message, StringComparison.Ordinal);

        // The acceptance criteria are the change request's job, not the commit's.
        Assert.DoesNotContain("Acceptance criteria", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubjectLongerThanSeventyTwoCharactersIsTrimmedAtAWordBoundary()
    {
        var title = string.Join(' ', Enumerable.Repeat("vertical", 20));
        var subject = ShimCommitMessage.Build($"# {title}\n").Split('\n')[0];

        Assert.True(subject.Length <= ShimCommitMessage.MaxSubjectLength);
        Assert.EndsWith("vertical", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributionInTheSpecificationNeverReachesTheCommitMessage()
    {
        // The specification is model-authored (section 16), so a byline can arrive in it as ordinary
        // prose. Section 24's rule is absolute regardless of how the text got there.
        var message = ShimCommitMessage.Build(
            "# Add the quote wizard\n\nCo-authored-by: Some Assistant <bot@example.test>\n"
            + "The wizard collects the vertical first.\n");

        Assert.DoesNotContain("Co-authored-by", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The wizard collects the vertical first.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Co-authored-by: A B <a@b.test>")]
    [InlineData("🤖 Generated with an agent")]
    [InlineData("Generated by a coding agent")]
    [InlineData("Written by an assistant")]
    public void ABylineIsRecognisedWhereverItAppears(string line)
        => Assert.True(ShimCommitMessage.LooksLikeAttribution(line));

    [Theory]
    [InlineData("Add the Claude Code adapter")]
    [InlineData("Qualify bare model identifiers as Anthropic")]
    public void AVendorNameThatDescribesTheChangeIsNotABylineAndIsKept(string title)
    {
        // Section 24 bans attribution, not vocabulary: a product name is an ordinary technical term
        // and belongs in a message whenever it is what the change is about.
        Assert.False(ShimCommitMessage.LooksLikeAttribution(title));
        Assert.Equal(title, ShimCommitMessage.Build($"# {title}\n"));
    }

    [Fact]
    public void ASpecificationWithNoTitleStillProducesASubject()
        => Assert.Equal(ShimCommitMessage.FallbackSubject, ShimCommitMessage.Build(string.Empty));

    [Theory]
    [InlineData("Dana Okoro", "dana@example.test", "Dana Okoro <dana@example.test>")]
    [InlineData("Dana <injected@example.test> Okoro", "dana@example.test", "Dana injected@example.test Okoro <dana@example.test>")]
    public void AnIdentityCannotSmuggleExtraHeadersIntoTheCommitObject(string name, string email, string expected)
        => Assert.Equal(expected, ShimCommitIdentity.TryCreate(name, email)!.Format());

    [Theory]
    [InlineData(null, "dana@example.test")]
    [InlineData("Dana Okoro", null)]
    [InlineData("Dana Okoro", "not-an-address")]
    [InlineData("<>", "dana@example.test")]
    public void AnIdentityThatIsNotUsableIsNoIdentity(string? name, string? email)
        => Assert.Null(ShimCommitIdentity.TryCreate(name, email));
}
