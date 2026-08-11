using Charter.Orchestration;
using Charter.Runners;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The run reference a runner reports, and the two things Charter must not do with it (sections 2.1,
/// 11, 16).
/// </summary>
/// <remarks>
/// <para>
/// <c>run_url</c> comes from the execution plane. The shim that posts it holds
/// <c>CHARTER_EVENT_TOKEN</c> in the same process as an agent reading untrusted repository content,
/// so section 16 makes it attacker-influenced input rather than a fact. Charter nevertheless folded
/// it into the session's external reference and then parsed the <em>repository</em> back out of it,
/// so cancelling one session issued
/// <c>POST /repos/{whatever-the-url-said}/actions/runs/{id}/cancel</c> with the instance's own
/// credential.
/// </para>
/// <para>
/// Two harms, and the tests below are split along them. The loud one is a write against another
/// repository connected to the same instance. The quiet one is that the run the requester asked to
/// stop was never touched, and section 11's cancel path was told it had been — so the session settles
/// as cancelled while an agent keeps running and keeps spending.
/// </para>
/// </remarks>
public class RunnerRunReferenceTests
{
    [Fact]
    public async Task ASessionStartedEventNamingAnotherConnectedRepositoryIsRefused()
    {
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.IngestAsync(
            world.SessionId,
            "session_started",
            $$"""{"run_url":"{{world.CrossRepoRunUrl}}"}""");

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);

        // And nothing was written: the point is not that the reference is unused, it is that it never
        // becomes the session's reference in the first place.
        Assert.Null(await world.ExternalReferenceAsync(world.SessionId));
    }

    [Fact]
    public async Task NoCrossRepositoryCallIsEverIssuedForARefusedEvent()
    {
        // The end of the chain the previous test cuts: refuse the event, and the cancel that follows
        // has nothing to point at somebody else's repository.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.IngestAsync(
            world.SessionId,
            "session_started",
            $$"""{"run_url":"{{world.CrossRepoRunUrl}}"}""");

        var github = new RecordingGitHubDispatcher();

        var result = await Runner(github).CancelAsync(new RunnerCancellation(
            world.SessionId,
            await world.ExternalReferenceAsync(world.SessionId),
            "Cancelled by request.",
            world.RepoFullName),
            TestContext.Current.CancellationToken);

        Assert.Empty(github.Cancellations);
        Assert.False(result.Stopped);
    }

    [Fact]
    public async Task TheCredentialExchangeRefusesTheSameLieAndMintsNothing()
    {
        // The other recording point. Fixing only one of the two implies a protection that is not
        // there, and this is the one that also hands out a contribute-scoped GitHub token.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(world.SessionId, runUrl: world.CrossRepoRunUrl);

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Empty(world.Broker.Issued);
        Assert.DoesNotContain(ExchangeWorld.GitHubToken, response.Body, StringComparison.Ordinal);
        Assert.Null(await world.ExternalReferenceAsync(world.SessionId));
    }

    [Fact]
    public async Task ALegitimateRunUrlStillWorksEndToEnd()
    {
        // The control. Every assertion above is a refusal, and a refusal proves nothing if the run
        // Charter itself dispatched can no longer report where it is or be cancelled.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var exchange = await world.ExchangeAsync(world.SessionId, runUrl: world.OwnRunUrl);
        Assert.Equal(StatusCodes.Status200OK, exchange.Status);
        Assert.Equal([world.RepoFullName], world.Broker.Issued);

        var started = await world.IngestAsync(
            world.SessionId,
            "session_started",
            $$"""{"run_url":"{{world.OwnRunUrl}}"}""");

        Assert.Equal(StatusCodes.Status200OK, started.Status);
        Assert.Equal(world.OwnRunUrl, await world.ExternalReferenceAsync(world.SessionId));

        var github = new RecordingGitHubDispatcher();

        var result = await Runner(github).CancelAsync(new RunnerCancellation(
            world.SessionId,
            await world.ExternalReferenceAsync(world.SessionId),
            "Cancelled by request.",
            world.RepoFullName),
            TestContext.Current.CancellationToken);

        Assert.True(result.Stopped);
        Assert.Equal([(world.RepoFullName, 900100L)], github.Cancellations);
    }

    [Fact]
    public async Task ACancelWillNotActOnAReferenceAlreadySittingInTheJournal()
    {
        // Rows written before the callback validation existed are still in the database, and an
        // upgrade does not rewrite them. So the backend has to refuse them too.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.PoisonAsync(world.SessionId, world.CrossRepoRunUrl);

        Assert.Equal(world.CrossRepoRunUrl, await world.ExternalReferenceAsync(world.SessionId));

        var github = new RecordingGitHubDispatcher();

        var result = await Runner(github).CancelAsync(new RunnerCancellation(
            world.SessionId,
            await world.ExternalReferenceAsync(world.SessionId),
            "Cancelled by request.",
            world.RepoFullName),
            TestContext.Current.CancellationToken);

        Assert.Empty(github.Cancellations);
        Assert.False(result.Stopped);
        Assert.Contains("has not been stopped", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelWithNoUsableRunReferenceDoesNotReportConfirmed()
    {
        var github = new RecordingGitHubDispatcher();
        var runner = Runner(github);
        var session = Guid.NewGuid();

        // Nothing reported yet; a reference the parser cannot use; a reference for another repository;
        // and a session whose own repository could not be resolved. None of these stopped anything.
        RunnerCancellation[] unusable =
        [
            new(session, null, "stop", "acme/widgets"),
            new(session, string.Empty, "stop", "acme/widgets"),
            new(session, "https://github.com/acme/widgets/actions", "stop", "acme/widgets"),
            new(session, "https://github.com/other/repo/actions/runs/7", "stop", "acme/widgets"),
            new(session, "https://github.com/acme/widgets/actions/runs/7", "stop", null),
        ];

        foreach (var cancellation in unusable)
        {
            var result = await runner.CancelAsync(cancellation, TestContext.Current.CancellationToken);

            Assert.False(result.Stopped);
            Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
        }

        Assert.Empty(github.Cancellations);
    }

    [Fact]
    public async Task AWorkflowRunThatAlreadyFinishedIsNotReportedAsStopped()
    {
        var github = new RecordingGitHubDispatcher { CancelResult = false };

        var result = await Runner(github).CancelAsync(new RunnerCancellation(
            Guid.NewGuid(),
            "https://github.com/acme/widgets/actions/runs/7",
            "stop",
            "acme/widgets"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Stopped);
        Assert.Single(github.Cancellations);
    }

    [Theory]

    // The control-plane handles the other two backends mint for themselves. A runner that could put
    // one of these in the journal would have DockerRunner kill an arbitrary container on the
    // operator's host, and AgentRunner cancel somebody else's job.
    [InlineData("charter-agent:job:5a5b1e30-0000-4000-8000-000000000000")]
    [InlineData("3f9a1c04d2b7e8115c6a")]

    // Not a run URL at all.
    [InlineData("https://github.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets/actions/runs/not-a-number")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]

    // Another repository, in the shapes a reader skims past.
    [InlineData("https://github.com/someone-else/private-repo/actions/runs/7")]
    [InlineData("https://github.com/acme/widgets-staging/actions/runs/7")]
    public void AReferenceThatIsNotThisSessionsRunIsRejected(string runUrl)
    {
        var check = RunnerRunReference.Evaluate(runUrl, "acme/widgets");

        Assert.True(check.IsRejected);
        Assert.False(check.IsRecordable);
        Assert.False(string.IsNullOrWhiteSpace(check.Refusal));
    }

    [Fact]
    public void ARunUrlForTheSessionsOwnRepositoryIsAccepted()
    {
        Assert.True(RunnerRunReference.Evaluate(
            "https://github.com/acme/widgets/actions/runs/7",
            "acme/widgets").IsRecordable);

        // GitHub repository names are case-insensitive, so a workflow reporting the canonical casing
        // against a row stored in another must not be read as an attack.
        Assert.True(RunnerRunReference.Evaluate(
            "https://github.com/Acme/Widgets/actions/runs/7",
            "acme/widgets").IsRecordable);

        // GitHub Enterprise puts the same path under another host.
        Assert.True(RunnerRunReference.Evaluate(
            "https://ghe.corp.example/acme/widgets/actions/runs/7",
            "acme/widgets").IsRecordable);
    }

    [Fact]
    public void NoReferenceAtAllIsNeitherAcceptedNorRefused()
    {
        foreach (var absent in new[] { null, string.Empty, "   " })
        {
            var check = RunnerRunReference.Evaluate(absent, "acme/widgets");

            Assert.Equal(RunReferenceDecision.Absent, check.Decision);
            Assert.False(check.IsRejected);
            Assert.False(check.IsRecordable);
        }
    }

    [Fact]
    public void ASessionWithNoResolvableRepositoryFailsClosed()
    {
        var check = RunnerRunReference.Evaluate("https://github.com/acme/widgets/actions/runs/7", null);

        Assert.True(check.IsRejected);
    }

    private static GitHubActionsRunner Runner(RecordingGitHubDispatcher github)
        => new(github, new GitHubActionsRunnerOptions(), NullLogger<GitHubActionsRunner>.Instance);
}
