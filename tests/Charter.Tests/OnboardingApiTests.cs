using Charter.Api.Contracts;
using Charter.Api.Repos;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Onboarding;
using Charter.VersionControl;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The HTTP surface of section 9, against a real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Section 9's flow was complete and had no way in. What these assert is that giving it one did not
/// give it a second set of rules: the ordering is still the state machine's, readiness is still only
/// ever earned by a passing smoke test, and a repository mid-onboarding is still not merely refused
/// to a requester but absent from what they can see at all.
/// </para>
/// <para>
/// The wizard's progress is read back out of the audit log, so these also pin that the smoke test's
/// outcome survives the request that reported it.
/// </para>
/// </remarks>
public class OnboardingApiTests
{
    [Fact]
    public async Task ConnectingIsAnAdminActionAndTheRepositoryStartsRequestableByNobody()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();

        var (refused, _) = await world.Repos.ConnectAsync(
            world.MemberOf(MemberRole.Engineer),
            world.ConnectBody(),
            TestContext.Current.CancellationToken);

        Assert.False(refused.Succeeded);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.Status);

        var (outcome, connected) = await world.Repos.ConnectAsync(
            world.Admin,
            world.ConnectBody(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(connected);
        Assert.Equal(ApiRepoStatus.Pending, connected.Repo.Status);
        Assert.False(connected.Repo.RequesterVisible);

        // Section 7.3: the one grant a newly connected repository gets is the person who connected
        // it. Nobody else has a row, and the absence of one is the refusal.
        var grants = await world.Db.RepoScopes
            .Where(scope => scope.RepoId == Guid.Parse(connected.Repo.Id))
            .ToListAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(grants);
        Assert.Equal(world.Admin.MemberId, only.MemberId);
        Assert.Null(only.Role);

        // And it still is not requestable, by them or anyone: readiness is a separate condition and
        // it has not been earned.
        Assert.False(await world.CanFileAsync(world.Admin, Guid.Parse(connected.Repo.Id)));
    }

    [Fact]
    public async Task GrantingAccessIsOneRowAtATimeAndWithholdingBeatsGranting()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();
        var requester = await world.RequesterMemberAsync();

        // Nothing granted, nothing visible, whatever the status.
        await world.SetStatusAsync(repo.Id, RepoStatus.Ready);
        Assert.Empty(await world.RequestableAsync(requester));

        var (granted, access) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { Role = ApiRole.Requester, CanRequest = true },
            TestContext.Current.CancellationToken);

        Assert.True(granted.Succeeded);
        Assert.Contains(access!.Grants, grant => grant.Role == ApiRole.Requester && grant.CanRequest);
        Assert.True(access.RequesterVisible);

        Assert.Single(await world.RequestableAsync(requester));

        // A withholding row addressed at this one person beats the permissive role grant.
        var (withheld, _) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { MemberId = requester.MemberId.ToString(), CanRequest = false },
            TestContext.Current.CancellationToken);

        Assert.True(withheld.Succeeded);
        Assert.Empty(await world.RequestableAsync(requester));

        // Section 7.3, guardrail 5: both writes are attributable.
        var audit = await world.Db.AuditLogs
            .AsNoTracking()
            .Where(row => row.TargetId == repo.Id.ToString())
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(audit, row => row.Action == AuditActions.RepoScopeGranted);
        Assert.Contains(audit, row => row.Action == AuditActions.RepoScopeRevoked);
    }

    [Fact]
    public async Task AGrantMustNameOneThingAndOnlyAMemberOfThisOrganisation()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        var (both, _) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody
            {
                MemberId = Guid.CreateVersion7().ToString(),
                Role = ApiRole.Requester,
                CanRequest = true,
            },
            TestContext.Current.CancellationToken);

        var (neither, _) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { CanRequest = true },
            TestContext.Current.CancellationToken);

        var (stranger, _) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { MemberId = Guid.CreateVersion7().ToString(), CanRequest = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, both.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, neither.Status);
        Assert.Equal(StatusCodes.Status404NotFound, stranger.Status);
    }

    [Fact]
    public async Task AnEngineerCannotWidenWhoMayFileAgainstARepository()
    {
        // Section 7.1: repo scope is an admin column. An engineer configures the repository; they do
        // not decide who may ask it for things.
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        var (outcome, _) = await world.Repos.SetAccessAsync(
            world.MemberOf(MemberRole.Engineer),
            repo.Id,
            new RepoAccessGrantBody { Role = ApiRole.Requester, CanRequest = true },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.Status);
    }

    [Fact]
    public async Task ARequesterCannotSeeTheOnboardingSurfaceAtAll()
    {
        // Section 7.1: a requester never sees a repo name. The way that stays true is that these
        // endpoints refuse them, not that a projection hides fields.
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        var requester = world.MemberOf(MemberRole.Requester);

        var (list, _) = await world.Repos.ListAsync(requester, TestContext.Current.CancellationToken);
        var (describe, _) = await world.Repos.DescribeAsync(requester, repo.Id, TestContext.Current.CancellationToken);
        var (recon, _) = await world.Repos.StartReconAsync(requester, repo.Id, TestContext.Current.CancellationToken);
        var (smoke, _) = await world.Repos.SmokeTestAsync(requester, repo.Id, TestContext.Current.CancellationToken);

        foreach (var outcome in (CommandOutcome[])[list, describe, recon, smoke])
        {
            Assert.False(outcome.Succeeded);
            Assert.Equal(StatusCodes.Status403Forbidden, outcome.Status);
        }
    }

    [Fact]
    public async Task OnboardingAdvancesOnlyInLegalOrder()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        // Scope cannot be confirmed before recon has proposed one.
        var (early, earlyResult) = await world.Repos.ConfirmScopeAsync(
            world.Engineer,
            repo.Id,
            new ConfirmScopeBody(),
            TestContext.Current.CancellationToken);

        Assert.False(early.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, early.Status);
        Assert.Equal(ApiRepoStatus.Pending, earlyResult!.Status);

        // Recon starts from pending, and only once.
        Assert.True((await world.Repos.StartReconAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken)).Outcome.Succeeded);

        var (twice, _) = await world.Repos.StartReconAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.False(twice.Succeeded);

        // The execution plane reports back, and only then may scope be confirmed.
        await world.Onboarding.CompleteReconAsync(
            repo.Id,
            new ReconReport { ProposedAllow = ["src/Features/**"] },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (confirmed, result) = await world.Repos.ConfirmScopeAsync(
            world.Engineer,
            repo.Id,
            new ConfirmScopeBody { Allow = ["src/Features/**"] },
            TestContext.Current.CancellationToken);

        Assert.True(confirmed.Succeeded);
        Assert.Equal(ApiRepoStatus.SmokeTest, result!.Status);

        // Confirming scope is what queues the smoke test — the endpoint never runs one itself.
        Assert.Single(world.Dispatcher.SmokeTest);
    }

    [Fact]
    public async Task NoEndpointCanMakeARepositoryReady()
    {
        // Section 9: readiness is earned. There is no administrative override, and this is the test
        // that would fail if somebody added one.
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        foreach (var attempt in (Func<Task<CommandOutcome>>[])
                 [
                     async () => (await world.Repos.StartReconAsync(
                         world.Engineer, repo.Id, TestContext.Current.CancellationToken)).Outcome,
                     async () => (await world.Repos.ConfirmScopeAsync(
                         world.Engineer, repo.Id, new ConfirmScopeBody(), TestContext.Current.CancellationToken)).Outcome,
                     async () => (await world.Repos.PublishPrimerAsync(
                         world.Engineer,
                         repo.Id,
                         new PublishPrimerBody { Markdown = "# Primer" },
                         TestContext.Current.CancellationToken)).Outcome,
                 ])
        {
            await attempt();
        }

        var reread = await world.Db.Repos
            .AsNoTracking()
            .SingleAsync(row => row.Id == repo.Id, TestContext.Current.CancellationToken);

        Assert.NotEqual(RepoStatus.Ready, reread.Status);
        Assert.False(reread.IsRequesterVisible);
    }

    [Fact]
    public async Task AnUnOnboardedRepoIsAbsentFromWhatARequesterCanFileAgainst()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        // The most permissive grant possible, deliberately: this is about readiness, not scope.
        world.Db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester));
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var requester = await world.RequesterMemberAsync();

        foreach (var status in (RepoStatus[])
                 [RepoStatus.Pending, RepoStatus.Recon, RepoStatus.Configuring, RepoStatus.SmokeTest])
        {
            await world.SetStatusAsync(repo.Id, status);

            // `/api/projects`: not refused, absent. A requester cannot learn the repository exists.
            Assert.Empty(await world.Queries.ListProjectsAsync(requester, TestContext.Current.CancellationToken));

            // The list query and the single-repository authoriser agree, because they are two
            // expressions of one sentence.
            Assert.Empty(await world.RequestableAsync(requester));
            Assert.False(await world.CanFileAsync(requester, repo.Id));
        }

        await world.SetStatusAsync(repo.Id, RepoStatus.Ready);

        Assert.Single(await world.Queries.ListProjectsAsync(requester, TestContext.Current.CancellationToken));
        Assert.Single(await world.RequestableAsync(requester));
        Assert.True(await world.CanFileAsync(requester, repo.Id));
    }

    [Fact]
    public async Task TheSmokeTestOutcomeIsReadableAfterTheRunThatReportedIt()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        // Nothing has run yet, so the answer is "no run" rather than a 404 on a repository that
        // plainly exists.
        var (before, none) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.True(before.Succeeded);
        Assert.Null(none);

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing with { PullRequestNumber = 7 },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (after, outcome) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.True(after.Succeeded);
        Assert.NotNull(outcome);
        Assert.True(outcome.Passed);
        Assert.True(outcome.PreviewBound);
        Assert.Equal(7, outcome.PullRequestNumber);

        // And the repository is now the one thing a requester may be shown.
        var (_, described) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(ApiRepoStatus.Ready, described!.Repo.Status);
        Assert.True(described.Repo.RequesterVisible);
        Assert.True(described.Steps.Single(step => step.Id == ApiOnboardingStepId.SmokeTest).Done);
    }

    [Fact]
    public async Task AFailedSmokeTestIsReportedAsFailedAndLeavesTheRepositoryUnready()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            new SmokeTestReport { RequestFiled = true, Failures = ["the preview never came up"] },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, outcome) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.False(outcome!.Passed);

        var (_, described) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(ApiRepoStatus.SmokeTest, described!.Repo.Status);
        Assert.False(described.Repo.RequesterVisible);
    }

    [Fact]
    public async Task TheMergeGateAssessmentIsSurfacedWithItsWarning()
    {
        // Change spec 001 part A.5: supported is not configured, and an operator who was never told
        // has been misled about the strongest property Charter claims.
        await using var world = await OnboardingApiWorld.CreateAsync();
        world.Provider.Protection["main"] = new BranchProtectionStatus(
            Configured: false,
            RequiresReview: false,
            RequiredApprovals: 0,
            CodeOwnersReviewRequired: false);

        var repo = await world.ReachSmokeTestAsync();

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing,
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, described) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(described!.MergeGate);
        Assert.Equal("advisory", described.MergeGate.Enforcement);
        Assert.False(described.MergeGate.ProtectionConfigured);
        Assert.NotNull(described.MergeGate.Warning);

        // It warns; it never blocks. The repository is still ready.
        Assert.Equal(ApiRepoStatus.Ready, described.Repo.Status);
    }

    [Fact]
    public async Task ReconsProposalSurvivesTheRunThatProducedIt()
    {
        // Section 9 step 3: the proposed scope is what an engineer *edits* before the repository is
        // requestable. A proposal that only ever existed inside the pull request would leave the
        // confirmation screen with nothing to render.
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        await world.Repos.StartReconAsync(world.Engineer, repo.Id, TestContext.Current.CancellationToken);

        await world.Onboarding.CompleteReconAsync(
            repo.Id,
            new ReconReport
            {
                DetectedStack = ["dotnet:10", "node:22"],
                ProposedAllow = ["src/Features/**", "src/Web/Components/**"],

                // Recon reading a README that asks for infrastructure access. The floor refuses it,
                // and the refusal is shown rather than dropped quietly (section 16).
                ProposedDeny = ["docs/**"],
                Checks = [new CharterCheck("build", "dotnet build"), new CharterCheck("test", "dotnet test")],
                Seed = "dotnet run --project tools/Seed",
                ExistingGuidance = new ExistingAgentGuidance(null, "# AGENTS.md"),
            },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (outcome, described) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(described!.ProposedScope);

        var proposal = described.ProposedScope;

        Assert.Equal(["dotnet:10", "node:22"], proposal.DetectedStack);
        Assert.Contains(proposal.Commands, command => command.Command == "dotnet test");
        Assert.Contains(proposal.Commands, command => command.Label == "Seed");
        Assert.Equal(["AGENTS.md"], proposal.ImportedFrom);

        var allowed = proposal.Entries.Where(entry => entry.Allowed).Select(entry => entry.Path).ToList();
        Assert.Contains("src/Features/**", allowed);
        Assert.Contains("src/Web/Components/**", allowed);

        // The deny-by-default floor renders as locked rows with a reason, never as a toggle the
        // server would filter out anyway.
        var locked = proposal.Entries.Where(entry => entry.Locked == true).ToList();
        Assert.NotEmpty(locked);
        Assert.All(locked, entry => Assert.False(entry.Allowed));
        Assert.All(locked, entry => Assert.NotNull(entry.Reason));

        // A directory glob is a directory, so the tree does not render globs as files.
        Assert.Equal(
            "directory",
            Assert.Single(proposal.Entries, entry => entry.Path == "src/Features/**").Kind);
    }

    [Fact]
    public async Task NoProposalIsSentBeforeReconHasRun()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        var (_, described) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        // Absent, so the wizard offers the recon step rather than rendering an empty file tree.
        Assert.Null(described!.ProposedScope);

        var body = await ApiPayloads.RenderAsync(described);
        Assert.DoesNotContain("proposedScope", ApiPayloads.Keys(body));
        Assert.DoesNotContain("primerDraftMd", ApiPayloads.Keys(body));
    }

    [Fact]
    public async Task ThePrimerDraftIsScaffoldedFromReconAndThenBecomesThePublishedPage()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();

        await world.Repos.StartReconAsync(world.Engineer, repo.Id, TestContext.Current.CancellationToken);

        await world.Onboarding.CompleteReconAsync(
            repo.Id,
            new ReconReport
            {
                DetectedStack = ["dotnet:10"],
                ProposedAllow = ["src/Features/**"],
                Checks = [new CharterCheck("test", "dotnet test")],
                ProjectName = "Quote tool",
            },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, drafted) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(drafted!.PrimerDraftMd);
        Assert.Contains("# Quote tool", drafted.PrimerDraftMd, StringComparison.Ordinal);
        Assert.Contains("dotnet test", drafted.PrimerDraftMd, StringComparison.Ordinal);

        // Section 9 step 5: the engineer edits it. After publishing, the editor opens on the page
        // that is live rather than on the draft it was written over.
        await world.Repos.PublishPrimerAsync(
            world.Engineer,
            repo.Id,
            new PublishPrimerBody { Markdown = "# Quote tool\n\nWhat the sales team sends customers." },
            TestContext.Current.CancellationToken);

        var (_, published) = await world.Repos.DescribeAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "# Quote tool\n\nWhat the sales team sends customers.",
            published!.PrimerDraftMd);
        Assert.True(published.Repo.HasPrimer);
    }

    [Fact]
    public async Task AFailedSmokeTestSaysWhichOfTheSixBrokeAndWhichNeverRan()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            new SmokeTestReport { RequestFiled = true, AgentRan = true },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, outcome) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome!.Checkpoints);
        Assert.Equal(6, outcome.Checkpoints.Count);

        var byId = outcome.Checkpoints.ToDictionary(point => point.Id);

        Assert.Equal(ApiSmokeTestCheckpointState.Passed, byId[ApiSmokeTestCheckpointId.RequestFiled].State);
        Assert.Equal(ApiSmokeTestCheckpointState.Passed, byId[ApiSmokeTestCheckpointId.AgentRan].State);

        // The first thing that did not happen is the failure; everything after it never ran, and
        // saying it failed would send the engineer to the wrong subsystem.
        Assert.Equal(ApiSmokeTestCheckpointState.Failed, byId[ApiSmokeTestCheckpointId.ChecksPassed].State);
        Assert.Equal(ApiSmokeTestCheckpointState.Skipped, byId[ApiSmokeTestCheckpointId.PullRequest].State);
        Assert.Equal(ApiSmokeTestCheckpointState.Skipped, byId[ApiSmokeTestCheckpointId.UrlBound].State);
    }

    [Fact]
    public async Task AnEmptyPreviewWarnsOnAPassingRunRatherThanBlockingIt()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing with { PreviewHasData = false, PullRequestNumber = 12 },
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, outcome) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.True(outcome!.Passed);
        Assert.NotNull(outcome.Warnings);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("no data", StringComparison.Ordinal));

        // All six still passed. Seed data is optional (section 9), so this is a caveat on a good run.
        Assert.All(outcome.Checkpoints!, point => Assert.Equal(ApiSmokeTestCheckpointState.Passed, point.State));

        Assert.Contains(
            "#12",
            Assert.Single(
                outcome.Checkpoints!,
                point => point.Id == ApiSmokeTestCheckpointId.PullRequest).Detail ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassingRunWithNothingOddToSayCarriesNoWarningsKeyAtAll()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ReachSmokeTestAsync();

        await world.Onboarding.CompleteSmokeTestAsync(
            repo.Id,
            SmokeTestReport.Passing,
            world.Engineer.UserId,
            TestContext.Current.CancellationToken);

        var (_, outcome) = await world.Repos.SmokeTestAsync(
            world.Engineer,
            repo.Id,
            TestContext.Current.CancellationToken);

        var body = await ApiPayloads.RenderAsync(outcome);

        // Absent rather than `[]`: the client's test is presence, so an empty array would render an
        // empty warning strip (section 7.4's mechanism, used here for tidiness rather than secrecy).
        Assert.DoesNotContain("warnings", ApiPayloads.Keys(body));
    }

    [Fact]
    public async Task TheAccessListNamesThePersonRatherThanJustTheirId()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();
        var repo = await world.ConnectAsync();
        var requester = await world.RequesterMemberAsync();

        var (outcome, access) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { MemberId = requester.MemberId.ToString(), CanRequest = true },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(access);

        var granted = Assert.Single(access.Grants, grant => grant.MemberId == requester.MemberId.ToString());

        // An access list rendered as a column of opaque ids is a list nobody audits.
        Assert.Equal("Ayesha Rahman", granted.MemberName);
        Assert.NotNull(granted.MemberEmail);

        // A role grant names no person, and says so by absence rather than by an empty string.
        var (_, withRole) = await world.Repos.SetAccessAsync(
            world.Admin,
            repo.Id,
            new RepoAccessGrantBody { Role = ApiRole.Requester, CanRequest = true },
            TestContext.Current.CancellationToken);

        var roleGrant = Assert.Single(withRole!.Grants, grant => grant.Role == ApiRole.Requester);

        Assert.Null(roleGrant.MemberName);
        Assert.Null(roleGrant.MemberId);
    }

    [Fact]
    public async Task ConnectingWithoutAnInstallationSaysWhatToDoRatherThanFailing()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();

        var (outcome, _) = await world.Repos.ConnectAsync(
            world.Admin,
            new ConnectRepoBody { FullName = "acme/widgets" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, outcome.Status);
        Assert.Contains("GitHub App", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryInAnotherOrganisationIsNotFoundRatherThanForbidden()
    {
        await using var world = await OnboardingApiWorld.CreateAsync();

        var (outcome, _) = await world.Repos.DescribeAsync(
            world.Engineer,
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status404NotFound, outcome.Status);
    }

    /// <summary>An organisation, an engineer, and the section 9 surface over a throwaway Postgres.</summary>
    private sealed class OnboardingApiWorld : IAsyncDisposable
    {
        private readonly Organization organization;

        private OnboardingApiWorld(CharterDbContext db, Organization organization, string repoName)
        {
            Db = db;
            this.organization = organization;
            RepoName = repoName;

            Github = new FakeRepositoryClient { BranchHeads = { ["main"] = "basesha" } };
            Folders = new FakeOnboardingFolderLoader();
            Dispatcher = new RecordingOnboardingDispatcher();

            Provider = new FakeVersionControlProvider();
            Provider.Protection["main"] = new BranchProtectionStatus(
                Configured: true,
                RequiresReview: true,
                RequiredApprovals: 1,
                CodeOwnersReviewRequired: true);

            var audit = new AuditWriter(db, CharterTime.System);

            Onboarding = new OnboardingService(
                db,
                Github,
                Folders,
                Dispatcher,
                new MergeGateInspector(
                    new VersionControlProviderRegistry([Provider]),
                    NullLogger<MergeGateInspector>.Instance),
                audit,
                CharterTime.System,
                NullLogger<OnboardingService>.Instance);

            Repos = new RepoOnboardingService(db, Onboarding, new RepoScopeAdministration(db, audit));
            Query = new RequestableRepoQuery(db);

            Queries = new RequestQueryService(
                db,
                new CharterAuthorizationService(db, audit),
                new VersionControlProviderRegistry([Provider]),
                CharterTime.System);
        }

        public CharterDbContext Db { get; }

        public string RepoName { get; }

        public FakeRepositoryClient Github { get; }

        public FakeOnboardingFolderLoader Folders { get; }

        public RecordingOnboardingDispatcher Dispatcher { get; }

        public FakeVersionControlProvider Provider { get; }

        public OnboardingService Onboarding { get; }

        public RepoOnboardingService Repos { get; }

        public RequestableRepoQuery Query { get; }

        public RequestQueryService Queries { get; }

        public MemberSnapshot Admin { get; private set; } = null!;

        public MemberSnapshot Engineer => Admin;

        public static async Task<OnboardingApiWorld> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("CHARTER_TEST_DATABASE_URL");

            Assert.SkipWhen(
                string.IsNullOrWhiteSpace(url),
                "Set CHARTER_TEST_DATABASE_URL to a throwaway Postgres to run the onboarding API tests.");

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url!));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create($"onboarding-api-{tag}", OrganizationMode.Organization);
            var user = User.Create($"engineer-{tag}@example.test", "Ellis Engineer");
            var member = Member.Create(organization.Id, user.Id, Member.AllRoles);

            db.Organizations.Add(organization);
            db.Users.Add(user);
            db.Members.Add(member);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new OnboardingApiWorld(db, organization, $"acme/widgets-{tag[..8]}")
            {
                Admin = MemberSnapshot.From(member),
            };
        }

        public ConnectRepoBody ConnectBody() => new()
        {
            FullName = RepoName,
            InstallationId = 4242,
            BaseBranch = "main",
        };

        public async Task<Repo> ConnectAsync()
            => await Onboarding.ConnectAsync(
                organization.Id,
                4242,
                RepoName,
                cancellationToken: TestContext.Current.CancellationToken);

        public async Task<Repo> ReachSmokeTestAsync()
        {
            var repo = await ConnectAsync();

            await Repos.StartReconAsync(Engineer, repo.Id, TestContext.Current.CancellationToken);

            await Onboarding.CompleteReconAsync(
                repo.Id,
                new ReconReport { ProposedAllow = ["src/Features/**"] },
                Engineer.UserId,
                TestContext.Current.CancellationToken);

            await Repos.ConfirmScopeAsync(
                Engineer,
                repo.Id,
                new ConfirmScopeBody(),
                TestContext.Current.CancellationToken);

            return repo;
        }

        public async Task SetStatusAsync(Guid repoId, RepoStatus status)
        {
            var repo = await Db.Repos.SingleAsync(row => row.Id == repoId, TestContext.Current.CancellationToken);

            repo.TransitionTo(status);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Db.ChangeTracker.Clear();
        }

        /// <summary>A real requester member row, so the query path has something to resolve.</summary>
        public async Task<MemberSnapshot> RequesterMemberAsync()
        {
            var tag = Guid.CreateVersion7().ToString("N");
            var user = User.Create($"requester-{tag}@example.test", "Ayesha Rahman");
            var member = Member.Create(organization.Id, user.Id, [MemberRole.Requester]);

            Db.Users.Add(user);
            Db.Members.Add(member);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Db.ChangeTracker.Clear();

            return MemberSnapshot.From(member);
        }

        public MemberSnapshot MemberOf(params MemberRole[] roles) => Admin with { Roles = roles };

        public Task<IReadOnlyList<RequestableRepo>> RequestableAsync(MemberSnapshot member)
            => Query.ListAsync(member.OrgId, member.MemberId, member.Roles, TestContext.Current.CancellationToken);

        public Task<bool> CanFileAsync(MemberSnapshot member, Guid repoId)
            => Query.CanFileAgainstAsync(
                member.OrgId,
                member.MemberId,
                member.Roles,
                repoId,
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}

/// <summary>A folder loader with a well-formed <c>.charter/</c> on the base branch.</summary>
internal sealed class FakeOnboardingFolderLoader : ICharterFolderLoader
{
    public CharterFolder Folder { get; set; } = CharterFolder.FromFiles(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".charter/config.yml"] = "version: 1\nscopes:\n  allow:\n    - \"src/Features/**\"\n",
        },
        "basesha");

    public Task<CharterFolder> LoadAsync(
        Charter.GitHub.GitHubRepository repository,
        string commitSha,
        CancellationToken cancellationToken = default) => Task.FromResult(Folder);
}

/// <summary>A dispatcher that records rather than queues.</summary>
internal sealed class RecordingOnboardingDispatcher : IOnboardingRunDispatcher
{
    public List<OnboardingJobPayload> Recon { get; } = [];

    public List<OnboardingJobPayload> SmokeTest { get; } = [];

    public Task<Guid> DispatchReconAsync(OnboardingJobPayload payload, CancellationToken cancellationToken = default)
    {
        Recon.Add(payload);
        return Task.FromResult(Guid.CreateVersion7());
    }

    public Task<Guid> DispatchSmokeTestAsync(OnboardingJobPayload payload, CancellationToken cancellationToken = default)
    {
        SmokeTest.Add(payload);
        return Task.FromResult(Guid.CreateVersion7());
    }
}
