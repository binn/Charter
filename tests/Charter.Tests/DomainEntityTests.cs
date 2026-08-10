using System.Reflection;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Entity invariants — the rules that must hold whatever calls the domain, and that no database is
/// needed to check.
/// </summary>
public class DomainEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TimestampsAreNormalisedToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(5));

        var organization = Organization.Create("Mayer Solar", now: local);

        Assert.Equal(TimeSpan.Zero, organization.CreatedAt.Offset);
        Assert.Equal(local.ToUniversalTime(), organization.CreatedAt);
    }

    [Fact]
    public void PersonalModeIsAnOrganizationWithOneMemberHoldingEveryRole()
    {
        // Section 7.2: same tables, same authorisation path. Only the seeded defaults differ.
        var organization = Organization.Create("Personal", OrganizationMode.Personal, Now);
        var member = Member.Create(organization.Id, Guid.CreateVersion7(), Member.AllRoles, now: Now);

        Assert.Equal(Enum.GetValues<MemberRole>().Length, member.Roles.Count);
        Assert.All(Enum.GetValues<MemberRole>(), role => Assert.True(member.HasRole(role)));

        // Inviting a second user is the only thing that changes, and it needs no migration.
        organization.PromoteToOrganization();
        Assert.Equal(OrganizationMode.Organization, organization.Mode);
    }

    [Fact]
    public void RolesAreAdditiveDeduplicatedAndNeverEmptied()
    {
        var member = Member.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), [MemberRole.Requester], now: Now);

        member.GrantRole(MemberRole.Engineer);
        member.GrantRole(MemberRole.Engineer);

        Assert.Equal([MemberRole.Requester, MemberRole.Engineer], member.Roles);

        member.RevokeRole(MemberRole.Engineer);
        Assert.Equal([MemberRole.Requester], member.Roles);

        Assert.Throws<InvalidOperationException>(() => member.RevokeRole(MemberRole.Requester));
        Assert.Throws<ArgumentException>(() => Member.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), []));
    }

    [Fact]
    public void RepoCreationCapabilityIsSeparateFromEveryRole()
    {
        // Section 26.10: repo creation is a privilege escalation, not a role.
        var member = Member.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Member.AllRoles, now: Now);

        Assert.False(member.HasCapability(MemberCapability.CanCreateRepo));

        member.GrantCapability(MemberCapability.CanCreateRepo);
        Assert.True(member.HasCapability(MemberCapability.CanCreateRepo));
    }

    [Fact]
    public void AConnectedRepoIsInvisibleToRequestersUntilItsSmokeTestPasses()
    {
        // Section 9: readiness is earned.
        var repo = Repo.Connect(Guid.CreateVersion7(), 4242, "mayersolar/spectra", now: Now);

        Assert.Equal(RepoStatus.Pending, repo.Status);
        Assert.False(repo.IsRequesterVisible);

        repo.TransitionTo(RepoStatus.SmokeTest, Now);
        Assert.False(repo.IsRequesterVisible);

        repo.TransitionTo(RepoStatus.Ready, Now);
        Assert.True(repo.IsRequesterVisible);
    }

    [Fact]
    public void ARepoScopeAddressesEitherAMemberOrARoleNeverBoth()
    {
        var repoId = Guid.CreateVersion7();

        var forMember = RepoScope.ForMember(repoId, Guid.CreateVersion7(), now: Now);
        Assert.NotNull(forMember.MemberId);
        Assert.Null(forMember.Role);

        var forRole = RepoScope.ForRole(repoId, MemberRole.Engineer, now: Now);
        Assert.Null(forRole.MemberId);
        Assert.NotNull(forRole.Role);
    }

    [Fact]
    public void AutoDispatchResolutionIsMostSpecificWins()
    {
        // Section 7.5: user override, then role, then repo default, then org default.
        var orgId = Guid.CreateVersion7();
        var repoId = Guid.CreateVersion7();

        var orgDefault = AutoDispatchPolicy.Create(orgId, enabled: false, now: Now);
        var repoDefault = AutoDispatchPolicy.Create(orgId, enabled: true, repoId: repoId, now: Now);
        var byRole = AutoDispatchPolicy.Create(orgId, enabled: true, role: MemberRole.Engineer, now: Now);
        var byUser = AutoDispatchPolicy.Create(orgId, enabled: true, userId: Guid.CreateVersion7(), now: Now);

        Assert.True(orgDefault.Specificity < repoDefault.Specificity);
        Assert.True(repoDefault.Specificity < byRole.Specificity);
        Assert.True(byRole.Specificity < byUser.Specificity);
    }

    [Fact]
    public void ASpecIsApprovedOnceAndOnlyOnce()
    {
        var spec = Spec.Draft(
            Guid.CreateVersion7(),
            version: 1,
            title: "Remember last selected vertical",
            outcome: "The wizard opens on the vertical you used last time.",
            bodyMd: "## Approach",
            acceptanceCriteria: """["Vertical is pre-selected on return"]""",
            now: Now);

        Assert.False(spec.IsApproved);

        var approver = Guid.CreateVersion7();
        spec.Approve(approver, Now);

        Assert.True(spec.IsApproved);
        Assert.Equal(approver, spec.ApprovedBy);
        Assert.Throws<InvalidOperationException>(() => spec.Approve(Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void CancellingASessionIsIdempotentAndRecordsTheFirstRequest()
    {
        var session = Session.Queue(Guid.CreateVersion7(), RunnerKind.Agent, "anthropic/claude-opus-5", now: Now);

        session.RequestCancellation(Now);
        session.RequestCancellation(Now.AddMinutes(5));

        Assert.Equal(Now, session.CancelRequestedAt);
    }

    [Fact]
    public void TakingOverASessionIsTerminalAndStopsAgentWrites()
    {
        // Section 7.5: an agent and a human editing the same branch is the destructive failure mode.
        var session = Session.Queue(Guid.CreateVersion7(), RunnerKind.Docker, "anthropic/claude-opus-5", now: Now);
        session.Start("a3f9c21", Now);

        session.HandOff(Now.AddMinutes(20));

        Assert.Equal(SessionStatus.HandedOff, session.Status);
        Assert.True(session.IsTerminal);
        Assert.Equal(Now.AddMinutes(20), session.EndedAt);
    }

    [Fact]
    public void SessionCostOnlyAccumulates()
    {
        var session = Session.Queue(Guid.CreateVersion7(), RunnerKind.Agent, "anthropic/claude-opus-5", now: Now);

        session.AddCost(1.25m);
        session.AddCost(0.75m);

        Assert.Equal(2.00m, session.CostUsd);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AddCost(-0.01m));
    }

    [Fact]
    public void AnEventSequenceStartsAtOneAndIsAppendOnly()
    {
        var sessionId = Guid.CreateVersion7();

        var first = Event.Append(sessionId, 1, EventTypes.SessionStarted, "{}", Now);

        Assert.Equal(1, first.Seq);
        Assert.Throws<ArgumentOutOfRangeException>(() => Event.Append(sessionId, 0, EventTypes.Message, "{}", Now));

        // No setters, no mutators: the transcript is written once and never edited.
        var mutators = typeof(Event)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true });

        Assert.Empty(mutators);
    }

    [Fact]
    public void AnUnknownEventTypeIsStoredRatherThanRejected()
    {
        // Adapters are data, not code (section 12b): a type this build has never seen must persist.
        var @event = Event.Append(Guid.CreateVersion7(), 7, "pi.custom_thing", """{"tool":"edit"}""", Now);

        Assert.Equal("pi.custom_thing", @event.Type);
    }

    [Fact]
    public void MeteredSpendReportsTheSameFigureInBothUnits()
    {
        var entry = LedgerEntry.ReserveUsd(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            LedgerCategory.Build,
            usd: 3.50m,
            now: Now);

        Assert.Equal(LedgerUnit.Usd, entry.Unit);
        Assert.Equal(3.50m, entry.Usd);
        Assert.Equal(0m, entry.QuotaSessions);
        Assert.Equal(3.50m, entry.ImputedUsd);
        Assert.Equal(3.50m, entry.Amount);
    }

    [Fact]
    public void ASubscriptionSessionIsNeverReportedAsFree()
    {
        // Section 20b.5: reporting a subscription session as $0.00 makes budget dashboards lie.
        var entry = LedgerEntry.ReserveQuota(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            LedgerCategory.Build,
            quotaSessions: 1m,
            imputedUsd: 4.20m,
            now: Now);

        Assert.Equal(LedgerUnit.QuotaSessions, entry.Unit);
        Assert.Equal(0m, entry.Usd);
        Assert.Equal(1m, entry.QuotaSessions);
        Assert.Equal(4.20m, entry.ImputedUsd);
        Assert.Equal(1m, entry.Amount);
    }

    [Fact]
    public void ReservationsSettleOnceAndReleasedHoldsCostNothing()
    {
        var entry = LedgerEntry.ReserveUsd(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            LedgerCategory.Build,
            usd: 5m,
            reservedUntil: Now.AddMinutes(30),
            now: Now);

        Assert.Equal(LedgerState.Reserved, entry.State);

        entry.Settle(usd: 2.15m, quotaSessions: 0m, imputedUsd: 2.15m, Now.AddMinutes(10));

        Assert.Equal(LedgerState.Settled, entry.State);
        Assert.Equal(2.15m, entry.Usd);
        Assert.Null(entry.ReservedUntil);
        Assert.Throws<InvalidOperationException>(() => entry.Settle(1m, 0m, 1m, Now));
        Assert.Throws<InvalidOperationException>(() => entry.Release(Now));

        var cancelled = LedgerEntry.ReserveUsd(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            LedgerCategory.Build,
            usd: 5m,
            now: Now);

        cancelled.Release(Now);

        Assert.Equal(LedgerState.Released, cancelled.State);
        Assert.Equal(0m, cancelled.Usd);
        Assert.Equal(0m, cancelled.ImputedUsd);
    }

    [Fact]
    public void ACredentialGrantExposesNoPlaintext()
    {
        // Section 20b.2: ciphertext only, never returned to the UI, never logged.
        var plaintextish = typeof(CredentialGrant)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property =>
                (property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                 || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                 || property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase))
                && !property.Name.EndsWith("Encrypted", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(plaintextish);

        var encrypted = typeof(CredentialGrant).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name.EndsWith("Encrypted", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(encrypted);
        Assert.All(encrypted, property => Assert.Equal(typeof(byte[]), Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));

        // And nothing may be constructed without ciphertext.
        Assert.Throws<ArgumentException>(() => CredentialGrant.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CredentialKind.AnthropicOauth,
            []));
    }

    [Fact]
    public void AnExhaustedGrantBecomesUsableAgainOnlyAfterItsResetInstant()
    {
        var grant = CredentialGrant.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CredentialKind.AnthropicOauth,
            [1, 2, 3],
            now: Now);

        Assert.True(grant.IsUsableAt(Now));

        grant.MarkExhausted(Now.AddHours(3));

        Assert.False(grant.IsUsableAt(Now));
        Assert.True(grant.IsUsableAt(Now.AddHours(4)));

        grant.Revoke();
        Assert.False(grant.IsUsableAt(Now.AddHours(4)));
    }

    [Fact]
    public void SharedPoolingIsNeverTheDefault()
    {
        // Section 20b.7: opting in is an explicit action, with a one-time warning.
        var grant = CredentialGrant.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CredentialKind.AnthropicOauth,
            [1],
            now: Now);

        Assert.Equal(CredentialScope.Personal, grant.Scope);

        grant.JoinSharedPool();
        Assert.Equal(CredentialScope.SharedPool, grant.Scope);

        grant.LeaveSharedPool();
        Assert.Equal(CredentialScope.Personal, grant.Scope);
    }

    [Fact]
    public void ABudgetsReservedFloorCannotExceedTheBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Budget.Create(
            Guid.CreateVersion7(),
            "Ops Tooling",
            BudgetScopeType.Team,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 100m,
            reservedAmount: 150m));

        var budget = Budget.Create(
            Guid.CreateVersion7(),
            "Ops Tooling",
            BudgetScopeType.Team,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 1500m,
            reservedAmount: 50m,
            now: Now);

        // An empty category list governs everything (section 34.2).
        Assert.True(budget.Covers(LedgerCategory.Chat));
        Assert.True(budget.Covers(LedgerCategory.Build));
    }

    [Fact]
    public void ACampaignBudgetNeedsItsWindow()
    {
        Assert.Throws<ArgumentException>(() => Budget.Create(
            Guid.CreateVersion7(),
            "Launch push",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.OneOff,
            amount: 500m));

        var campaign = Budget.Create(
            Guid.CreateVersion7(),
            "Launch push",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.OneOff,
            amount: 500m,
            startsAt: Now,
            endsAt: Now.AddDays(30),
            now: Now);

        Assert.True(campaign.IsActiveAt(Now.AddDays(1)));
        Assert.False(campaign.IsActiveAt(Now.AddDays(31)));
    }

    [Fact]
    public void CategoryScopedBudgetsOnlyCoverTheirCategories()
    {
        var teaching = Budget.Create(
            Guid.CreateVersion7(),
            "Teaching",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 50m,
            categories: [LedgerCategory.Teach],
            now: Now);

        Assert.True(teaching.Covers(LedgerCategory.Teach));
        Assert.False(teaching.Covers(LedgerCategory.Build));
    }

    [Fact]
    public void AnExpiringPreviewIsADesignedStateRatherThanA404()
    {
        // Section 27.7: expired previews are the number one source of confusion in tools like this.
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), VerificationArtifactKind.HostedPreview, now: Now);

        Assert.Equal(VerificationArtifactState.Pending, artifact.DisplayStateAt(Now));

        artifact.MarkReady(url: "https://pr-142.example.dev", expiresAt: Now.AddHours(6));

        Assert.Equal(VerificationArtifactState.Ready, artifact.DisplayStateAt(Now));
        Assert.Equal(VerificationArtifactState.Expiring, artifact.DisplayStateAt(Now.AddHours(5).AddMinutes(30)));
        Assert.Equal(VerificationArtifactState.Expired, artifact.DisplayStateAt(Now.AddHours(7)));

        // And Expiring is derived, never stored.
        Assert.Equal(VerificationArtifactState.Ready, artifact.State);
    }

    [Fact]
    public void AReadyArtifactMustCarrySomethingToOpen()
    {
        var artifact = VerificationArtifact.Pending(Guid.CreateVersion7(), VerificationArtifactKind.BuildArtifact, now: Now);

        Assert.Throws<ArgumentException>(() => artifact.MarkReady());

        // Except `none`, which is honestly engineer-verified and has no action at all (section 27.4).
        var engineerOnly = VerificationArtifact.Pending(
            Guid.CreateVersion7(),
            VerificationArtifactKind.None,
            VerificationArtifactAudience.EngineerOnly,
            Now);

        engineerOnly.MarkReady(instructionsMd: "An engineer verifies this change.");
        Assert.Equal(VerificationArtifactState.Ready, engineerOnly.State);
    }

    [Fact]
    public void ADeploymentIsOnlyReadyWhenItHasSomewhereToGo()
        => Assert.Throws<ArgumentException>(() => Deployment.Report(
            Guid.CreateVersion7(),
            "railway",
            DeploymentState.Ready,
            now: Now));

    [Fact]
    public void PushingANewCommitClearsStaleness()
    {
        var pullRequest = PullRequest.Open(Guid.CreateVersion7(), 142, "https://github.com/o/r/pull/142", "a3f9c21", now: Now);

        pullRequest.MarkStale(Now);
        Assert.True(pullRequest.IsStale);

        pullRequest.UpdateState(PullRequestState.Open, "b7e0d13", Now.AddMinutes(1));
        Assert.False(pullRequest.IsStale);
        Assert.Equal("b7e0d13", pullRequest.HeadSha);
    }

    [Fact]
    public void TheConceptLedgerCountsReferencesAndCanBeReset()
    {
        var entry = ConceptLedger.Record(Guid.CreateVersion7(), "  Migration ", Now);

        Assert.Equal("migration", entry.Concept);
        Assert.Equal(1, entry.TimesReferenced);

        entry.Reference(Now.AddDays(1));

        Assert.Equal(2, entry.TimesReferenced);
        Assert.Equal(Now.AddDays(1), entry.LastReferencedAt);
        Assert.Equal(Now, entry.FirstExplainedAt);
    }

    [Fact]
    public void AJobIsEnqueuedPendingAndAvailableImmediately()
    {
        var job = Job.Enqueue(JobType.Refine, """{"requestId":"..."}""", now: Now);

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Now, job.AvailableAt);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Job.DefaultMaxAttempts, job.MaxAttempts);
        Assert.Empty(job.RequiredCapabilities);
        Assert.False(job.IsLeaseExpiredAt(Now));
    }

    [Fact]
    public void ADelayedJobIsNotAvailableYet()
    {
        var job = Job.Enqueue(JobType.UpdateCheck, "{}", availableAt: Now.AddHours(24), now: Now);

        Assert.Equal(Now.AddHours(24), job.AvailableAt);
    }

    [Fact]
    public void OnlyAPendingJobCanBeCancelledThroughTheQueue()
    {
        var job = Job.Enqueue(JobType.Build, "{}", now: Now);

        job.Cancel(Now);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Throws<InvalidOperationException>(() => job.Cancel(Now));
    }

    [Fact]
    public void RequiredCapabilitiesAreNormalisedForArrayContainment()
    {
        // The claim query is a Postgres `<@` test, which is set-based: duplicates and stray spaces
        // would only make the stored array bigger, never more correct.
        var job = Job.Enqueue(
            JobType.Build,
            "{}",
            requiredCapabilities: [" macos ", "xcode:16", "macos"],
            now: Now);

        Assert.Equal(["macos", "xcode:16"], job.RequiredCapabilities);
    }

    [Fact]
    public void EmptyOrBlankRequiredTextIsRejectedEverywhere()
    {
        Assert.Throws<ArgumentException>(() => Organization.Create("   "));
        Assert.Throws<ArgumentException>(() => User.Create("", "Ayesha"));
        Assert.Throws<ArgumentException>(() => Repo.Connect(Guid.CreateVersion7(), 1, " "));
        Assert.Throws<ArgumentException>(() => Request.File(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), " "));
        Assert.Throws<ArgumentException>(() => Job.Enqueue(JobType.Build, " "));
    }

    [Fact]
    public void EmailIsNormalisedSoTheUniqueIndexMeansSomething()
    {
        var user = User.Create("  Ayesha@Example.COM ", "Ayesha", now: Now);

        Assert.Equal("ayesha@example.com", user.Email);
    }
}
