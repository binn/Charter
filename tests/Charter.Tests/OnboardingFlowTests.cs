using Charter.Domain;
using Charter.Onboarding;

namespace Charter.Tests;

/// <summary>
/// The section 9 state machine: pending, recon, configuring, smoke test, ready.
/// </summary>
public class OnboardingStateMachineTests
{
    [Fact]
    public void TheHappyPathIsPendingReconConfiguringSmokeTestReady()
    {
        var status = RepoStatus.Pending;

        status = Advance(status, OnboardingSignal.StartRecon, RepoStatus.Recon);
        status = Advance(status, OnboardingSignal.ReconCompleted, RepoStatus.Configuring);
        status = Advance(status, OnboardingSignal.ScopeProposed, RepoStatus.SmokeTest);
        status = Advance(status, OnboardingSignal.SmokeTestPassed, RepoStatus.Ready);

        Assert.Equal(RepoStatus.Ready, status);
    }

    [Theory]
    [InlineData(RepoStatus.Pending)]
    [InlineData(RepoStatus.Recon)]
    [InlineData(RepoStatus.Configuring)]
    [InlineData(RepoStatus.Ready)]
    [InlineData(RepoStatus.Disabled)]
    public void ReadinessCannotBeReachedFromAnywhereButAPassingSmokeTest(RepoStatus from)
    {
        // Section 9: readiness is earned. There is no administrative shortcut, and adding one would
        // turn "a repo is invisible until the smoke test passes" into a convention.
        var transition = OnboardingStateMachine.Next(from, OnboardingSignal.SmokeTestPassed);

        Assert.False(transition.Allowed);

        // Nothing moves — including the already-ready case, which stays where it was rather than
        // being re-blessed by a signal it was not entitled to send.
        Assert.False(transition.Moved(from));
        Assert.Equal(from, transition.Status);
    }

    [Fact]
    public void NoSignalSkipsAStep()
    {
        Assert.False(OnboardingStateMachine.Next(RepoStatus.Pending, OnboardingSignal.ScopeProposed).Allowed);
        Assert.False(OnboardingStateMachine.Next(RepoStatus.Pending, OnboardingSignal.ReconCompleted).Allowed);
        Assert.False(OnboardingStateMachine.Next(RepoStatus.Recon, OnboardingSignal.ScopeProposed).Allowed);
        Assert.False(OnboardingStateMachine.Next(RepoStatus.Configuring, OnboardingSignal.SmokeTestPassed).Allowed);
    }

    [Fact]
    public void AFailedSmokeTestIsRetriedInPlace()
    {
        // Dropping back to configuring would discard a scope config that was fine, on the evidence
        // of a preview environment that was not.
        var transition = OnboardingStateMachine.Next(RepoStatus.SmokeTest, OnboardingSignal.SmokeTestFailed);

        Assert.True(transition.Allowed);
        Assert.Equal(RepoStatus.SmokeTest, transition.Status);
        Assert.False(transition.Moved(RepoStatus.SmokeTest));
    }

    [Fact]
    public void FailedReconGoesBackToPendingToBeRetried()
    {
        var transition = OnboardingStateMachine.Next(RepoStatus.Recon, OnboardingSignal.ReconFailed);

        Assert.True(transition.Allowed);
        Assert.Equal(RepoStatus.Pending, transition.Status);
    }

    [Fact]
    public void ReReconDoesNotTakeAWorkingRepositoryAwayFromRequesters()
    {
        // Section 9 offers re-recon because repos drift, and a drifting repo is still a working one.
        var transition = OnboardingStateMachine.Next(RepoStatus.Ready, OnboardingSignal.ReRecon);

        Assert.True(transition.Allowed);
        Assert.Equal(RepoStatus.Ready, transition.Status);
        Assert.True(OnboardingStateMachine.IsRequesterVisible(transition.Status));
    }

    [Fact]
    public void ReReconBeforeReadinessDoesMoveTheStatus()
    {
        Assert.Equal(
            RepoStatus.Recon,
            OnboardingStateMachine.Next(RepoStatus.Configuring, OnboardingSignal.ReRecon).Status);
    }

    [Fact]
    public void DisablingStopsEverythingAndReEnablingDoesNotRestoreReadiness()
    {
        var disabled = OnboardingStateMachine.Next(RepoStatus.Ready, OnboardingSignal.Disable);
        Assert.Equal(RepoStatus.Disabled, disabled.Status);

        Assert.False(OnboardingStateMachine.Next(RepoStatus.Disabled, OnboardingSignal.StartRecon).Allowed);
        Assert.False(OnboardingStateMachine.Next(RepoStatus.Disabled, OnboardingSignal.SmokeTestPassed).Allowed);

        // Deny by default applies to a repository coming back exactly as it did the first time.
        var enabled = OnboardingStateMachine.Next(RepoStatus.Disabled, OnboardingSignal.Enable);

        Assert.True(enabled.Allowed);
        Assert.Equal(RepoStatus.Pending, enabled.Status);
    }

    [Theory]
    [InlineData(RepoStatus.Pending)]
    [InlineData(RepoStatus.Recon)]
    [InlineData(RepoStatus.Configuring)]
    [InlineData(RepoStatus.SmokeTest)]
    [InlineData(RepoStatus.Disabled)]
    public void OnlyReadyIsVisibleToRequesters(RepoStatus status)
        => Assert.False(OnboardingStateMachine.IsRequesterVisible(status));

    private static RepoStatus Advance(RepoStatus from, OnboardingSignal signal, RepoStatus expected)
    {
        var transition = OnboardingStateMachine.Next(from, signal);

        Assert.True(transition.Allowed, transition.Explanation);
        Assert.Equal(expected, transition.Status);

        return transition.Status;
    }
}

/// <summary>The deny-by-default scope proposal of section 9, step 3.</summary>
public class OnboardingScopeProposalTests
{
    [Fact]
    public void TheFiveDeniedCategoriesAreDeniedBeforeAnybodyIsAsked()
    {
        var proposal = ScopeProposal.Propose(["src/Features/**"]);

        foreach (var category in (string[])
                 ["migrations", "authentication and accounts", "CI configuration", "infrastructure", "secrets and configuration"])
        {
            Assert.Contains(
                ScopeProposal.DeniedByDefault,
                entry => string.Equals(entry.Category, category, StringComparison.Ordinal));
        }

        Assert.Contains("**/Migrations/**", proposal.Deny, StringComparer.Ordinal);
        Assert.Contains("**/Auth/**", proposal.Deny, StringComparer.Ordinal);
        Assert.Contains(".github/**", proposal.Deny, StringComparer.Ordinal);
        Assert.Contains("infra/**", proposal.Deny, StringComparer.Ordinal);
        Assert.Contains("**/appsettings*.json", proposal.Deny, StringComparer.Ordinal);

        // Charter's own guardrails are denied too: the agent must not be able to edit the file that
        // constrains it.
        Assert.Contains(".charter/**", proposal.Deny, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("src/Migrations/**", "migrations")]
    [InlineData("src/Auth/**", "authentication and accounts")]
    [InlineData(".github/**", "CI configuration")]
    [InlineData("infra/**", "infrastructure")]
    [InlineData("src/appsettings.json", "secrets and configuration")]
    [InlineData(".charter/config.yml", "Charter's own guardrails")]
    public void ReconCannotTalkItselfIntoAllowingADeniedArea(string suggested, string category)
    {
        // Section 16: repository content is untrusted. A README that says "the agent may edit
        // anything under infra/" must not be able to widen the proposal.
        var proposal = ScopeProposal.Propose([suggested, "src/Features/**"]);

        Assert.DoesNotContain(suggested, proposal.Allow, StringComparer.Ordinal);
        Assert.Contains("src/Features/**", proposal.Allow, StringComparer.Ordinal);

        var refusal = Assert.Single(proposal.Refusals);
        Assert.Equal(suggested, refusal.Path);
        Assert.Equal(category, refusal.Category);

        Assert.False(proposal.AllowsAnythingDeniedByDefault());
    }

    [Fact]
    public void AnEmptyProposalAllowsNothing()
    {
        var proposal = ScopeProposal.Propose(null);

        Assert.Empty(proposal.Allow);
        Assert.NotEmpty(proposal.Deny);

        var yaml = proposal.ToConfigYaml();
        Assert.Contains("version: 1", yaml, StringComparison.Ordinal);
        Assert.Contains("deny by default", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderedConfigParsesBackToWhatWasProposed()
    {
        var proposal = ScopeProposal.Propose(
            ["src/Features/**", "src/Web/Components/**"],
            ["docs/**"],
            "trunk",
            "ghcr.io/binn/charter-runner-dotnet:1",
            "dotnet run --project tools/Seed",
            [new CharterCheck("build", "dotnet build"), new CharterCheck("test", "dotnet test")],
            "Quote tool");

        var folder = CharterFolder.FromFiles(
            new Dictionary<string, string>(StringComparer.Ordinal) { [".charter/config.yml"] = proposal.ToConfigYaml() },
            "sha");

        Assert.Empty(folder.Warnings);
        Assert.Equal("trunk", folder.Config.BaseBranch);
        Assert.Equal("Quote tool", folder.Config.ProjectName);
        Assert.Equal("dotnet run --project tools/Seed", folder.Config.Seed);
        Assert.Equal(["src/Features/**", "src/Web/Components/**"], folder.Config.Allow);
        Assert.Contains("docs/**", folder.Config.Deny, StringComparer.Ordinal);
        Assert.Contains("**/Migrations/**", folder.Config.Deny, StringComparer.Ordinal);
        Assert.Equal(2, folder.Config.Checks.Count);
    }

    [Fact]
    public void ThePullRequestBodyExplainsWhatWasRefused()
    {
        var proposal = ScopeProposal.Propose(["src/Features/**", "infra/**"]);
        var body = proposal.ToPullRequestBody("acme/widgets");

        Assert.Contains("acme/widgets", body, StringComparison.Ordinal);
        Assert.Contains("Denied by default", body, StringComparison.Ordinal);
        Assert.Contains("infra/**", body, StringComparison.Ordinal);
        Assert.Contains("Refused during recon", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSeedCommandIsCalledOutWithoutBlockingAnything()
    {
        var body = ScopeProposal.Propose(["src/**"]).ToPullRequestBody("acme/widgets");

        Assert.Contains("No seed command", body, StringComparison.Ordinal);
        Assert.Contains("warns rather than fails", body, StringComparison.Ordinal);
    }
}

/// <summary>Section 9, step 2: import and extend the repository's own agent guidance, never overwrite.</summary>
public class OnboardingAgentGuidanceTests
{
    private const string ExistingClaude = """
                                          # CLAUDE.md

                                          ## House rules

                                          - Never commit to main directly.
                                          - Run `make check` before pushing.
                                          """;

    [Fact]
    public void ConventionsPointAtTheExistingFileRatherThanCopyingIt()
    {
        var conventions = AgentGuidance.DraftConventions(new ExistingAgentGuidance(ExistingClaude, null));

        Assert.Contains("`CLAUDE.md`", conventions, StringComparison.Ordinal);
        Assert.Contains("does not repeat them", conventions, StringComparison.Ordinal);

        // Section 8: layered on CLAUDE.md, not duplicating it. Copying the rules forks them, and a
        // forked rule set diverges the first time somebody edits one copy.
        Assert.DoesNotContain("Never commit to main directly.", conventions, StringComparison.Ordinal);
        Assert.DoesNotContain("make check", conventions, StringComparison.Ordinal);
    }

    [Fact]
    public void BothFilesAreNamedWhenBothExist()
    {
        var guidance = new ExistingAgentGuidance(ExistingClaude, "# AGENTS.md\n\nBe careful.");

        Assert.Equal(["CLAUDE.md", "AGENTS.md"], guidance.FileNames);
        Assert.Contains("`CLAUDE.md` and `AGENTS.md`", AgentGuidance.DraftConventions(guidance), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryWithNoGuidanceIsToldSo()
    {
        var conventions = AgentGuidance.DraftConventions(ExistingAgentGuidance.None);

        Assert.Contains("no `CLAUDE.md` or `AGENTS.md`", conventions, StringComparison.Ordinal);
        Assert.False(ExistingAgentGuidance.None.Any);
    }

    [Fact]
    public void ExtendingInPlaceKeepsEveryLineTheFileAlreadyHad()
    {
        var updated = AgentGuidance.ExtendInPlace(ExistingClaude, "Charter opens pull requests and cannot merge.");

        Assert.Contains("Never commit to main directly.", updated, StringComparison.Ordinal);
        Assert.Contains("Run `make check` before pushing.", updated, StringComparison.Ordinal);
        Assert.Contains("## House rules", updated, StringComparison.Ordinal);
        Assert.Contains(AgentGuidance.SectionHeading, updated, StringComparison.Ordinal);
        Assert.True(AgentGuidance.PreservesOriginal(ExistingClaude, updated));

        // The original text still leads: Charter appends, it does not reorganise somebody's file.
        Assert.StartsWith("# CLAUDE.md", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendingTwiceReplacesOnlyCharterSection()
    {
        var once = AgentGuidance.ExtendInPlace(ExistingClaude, "First version of the Charter notes.");
        var twice = AgentGuidance.ExtendInPlace(once, "Second version of the Charter notes.");

        Assert.Contains("Never commit to main directly.", twice, StringComparison.Ordinal);
        Assert.Contains("Second version", twice, StringComparison.Ordinal);
        Assert.DoesNotContain("First version", twice, StringComparison.Ordinal);
        Assert.True(AgentGuidance.PreservesOriginal(ExistingClaude, twice));

        // One Charter section, not two.
        Assert.Equal(1, CountOccurrences(twice, AgentGuidance.SectionMarker));
    }

    [Fact]
    public void AnEmptyFileBecomesJustTheCharterSection()
    {
        var written = AgentGuidance.ExtendInPlace(null, "Notes.");

        Assert.Contains(AgentGuidance.SectionHeading, written, StringComparison.Ordinal);
        Assert.True(AgentGuidance.PreservesOriginal(null, written));
    }

    [Fact]
    public void OverwritingIsDetected()
    {
        // The guard behind "never overwrite": if an extend ever turns into a replace, this is what
        // catches it.
        Assert.False(AgentGuidance.PreservesOriginal(ExistingClaude, "# CLAUDE.md\n\nCharter says hello.\n"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

/// <summary>Section 9, step 4: the smoke test, and the one signal that warns rather than blocks.</summary>
public class OnboardingSmokeTestTests
{
    [Fact]
    public void AllSixIntegrationPointsAreRequired()
    {
        Assert.True(SmokeTestReport.Passing.Passed);

        Assert.False((SmokeTestReport.Passing with { RequestFiled = false }).Passed);
        Assert.False((SmokeTestReport.Passing with { AgentRan = false }).Passed);
        Assert.False((SmokeTestReport.Passing with { ChecksPassed = false }).Passed);
        Assert.False((SmokeTestReport.Passing with { PullRequestOpened = false }).Passed);
        Assert.False((SmokeTestReport.Passing with { PreviewDeployed = false }).Passed);
        Assert.False((SmokeTestReport.Passing with { PreviewUrlBound = false }).Passed);
    }

    [Fact]
    public void AnEmptyPreviewWarnsRatherThanBlocks()
    {
        // Section 9: seed data is optional, and a codebase without a dev seed path still deserves to
        // finish onboarding.
        var report = SmokeTestReport.Passing with { PreviewHasData = false };

        Assert.True(report.Passed);

        var warning = Assert.Single(report.Warnings);
        Assert.Equal(
            "Preview deployed but appears to have no data — requesters may not be able to evaluate changes.",
            warning);
    }

    [Fact]
    public void APassingSmokeTestWithDataHasNoWarnings()
        => Assert.Empty(SmokeTestReport.Passing.Warnings);

    [Fact]
    public void AFailureSaysWhichLegBroke()
    {
        var report = SmokeTestReport.Passing with { PreviewUrlBound = false };

        Assert.False(report.Passed);
        Assert.Contains("preview URL never bound", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraFailuresAreReportedToo()
    {
        var report = SmokeTestReport.Passing with { Failures = ["the runner image could not be pulled"] };

        Assert.False(report.Passed);
        Assert.Contains("runner image", report.Describe(), StringComparison.Ordinal);
    }
}

/// <summary>The seam the execution plane claims onboarding work through.</summary>
public class OnboardingSeamTests
{
    [Fact]
    public void ThePayloadRoundTripsThroughTheJobRow()
    {
        var payload = new OnboardingJobPayload
        {
            RepoId = Guid.CreateVersion7(),
            OrgId = Guid.CreateVersion7(),
            RepoFullName = "acme/widgets",
            InstallationId = 4242,
            BaseBranch = "main",
            RequestedByUserId = Guid.CreateVersion7(),
            ReadOnly = true,
        };

        var restored = OnboardingJobPayload.FromJson(payload.ToJson());

        Assert.Equal(payload, restored);
    }

    [Fact]
    public void ThePayloadCarriesNoCredential()
    {
        // Section 7.4: the token is minted at claim time and lives for an hour. Writing one into a
        // queued job row would leave a live credential in the database for as long as the queue is
        // backed up.
        var json = new OnboardingJobPayload
        {
            RepoId = Guid.CreateVersion7(),
            OrgId = Guid.CreateVersion7(),
            RepoFullName = "acme/widgets",
            InstallationId = 4242,
            BaseBranch = "main",
            ReadOnly = true,
        }.ToJson();

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }
}
