using Charter.Data;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Pins the database spelling of every enumerated column in the data model.
/// </summary>
/// <remarks>
/// Specification section 5 writes these as <c>status(pending|recon|...)</c>, and they are stored as
/// text rather than as integers so a value inserted in the middle of an enum cannot silently
/// re-label existing rows. These assertions are the contract: changing a spelling here is a schema
/// change and needs a migration, which is exactly the conversation a failing test should start.
/// </remarks>
public class DomainEnumMappingTests
{
    [Fact]
    public void OrganizationModeMatchesTheSpecification()
        => AssertSpelling<OrganizationMode>(new()
        {
            [OrganizationMode.Personal] = "personal",
            [OrganizationMode.Organization] = "organization",
        });

    [Fact]
    public void MemberRoleMatchesTheSpecification()
        => AssertSpelling<MemberRole>(new()
        {
            [MemberRole.Requester] = "requester",
            [MemberRole.Approver] = "approver",
            [MemberRole.Engineer] = "engineer",
            [MemberRole.Admin] = "admin",
        });

    [Fact]
    public void MemberCapabilityMatchesTheSpecification()
        => AssertSpelling<MemberCapability>(new()
        {
            [MemberCapability.CanCreateRepo] = "can_create_repo",
        });

    [Fact]
    public void TeachingLevelMatchesTheSpecification()
        => AssertSpelling<TeachingLevel>(new()
        {
            [TeachingLevel.ExplainEverything] = "explain_everything",
            [TeachingLevel.SkipTheBasics] = "skip_the_basics",
            [TeachingLevel.JustTheDecisions] = "just_the_decisions",
        });

    [Fact]
    public void IdentityProviderMatchesTheSpecification()
        => AssertSpelling<IdentityProviderKind>(new()
        {
            [IdentityProviderKind.Password] = "password",
            [IdentityProviderKind.GitHub] = "github",
            [IdentityProviderKind.Google] = "google",
            [IdentityProviderKind.Discord] = "discord",
            [IdentityProviderKind.Slack] = "slack",
            [IdentityProviderKind.Saml] = "saml",
        });

    [Fact]
    public void RepoStatusMatchesTheSpecification()
        => AssertSpelling<RepoStatus>(new()
        {
            [RepoStatus.Pending] = "pending",
            [RepoStatus.Recon] = "recon",
            [RepoStatus.Configuring] = "configuring",
            [RepoStatus.SmokeTest] = "smoke_test",
            [RepoStatus.Ready] = "ready",
            [RepoStatus.Disabled] = "disabled",
        });

    [Fact]
    public void RunnerKindMatchesTheConfigurationValues()
        => AssertSpelling<RunnerKind>(new()
        {
            [RunnerKind.Agent] = "agent",

            // CHARTER_RUNNER accepts `github-actions`, so the stored value spells it the same way.
            [RunnerKind.GitHubActions] = "github-actions",
            [RunnerKind.Docker] = "docker",
        });

    [Fact]
    public void LedgerCategoryMatchesTheSpecification()
        => AssertSpelling<LedgerCategory>(new()
        {
            [LedgerCategory.Build] = "build",
            [LedgerCategory.Teach] = "teach",
            [LedgerCategory.Refine] = "refine",
            [LedgerCategory.Recap] = "recap",
            [LedgerCategory.Recon] = "recon",
            [LedgerCategory.Scaffold] = "scaffold",
            [LedgerCategory.Chat] = "chat",
        });

    [Fact]
    public void LedgerUnitAndStateMatchTheSpecification()
    {
        AssertSpelling<LedgerUnit>(new()
        {
            [LedgerUnit.Usd] = "usd",
            [LedgerUnit.QuotaSessions] = "quota_sessions",
        });

        AssertSpelling<LedgerState>(new()
        {
            [LedgerState.Reserved] = "reserved",
            [LedgerState.Settled] = "settled",
            [LedgerState.Released] = "released",
        });
    }

    [Fact]
    public void CredentialGrantEnumsMatchTheSpecification()
    {
        AssertSpelling<CredentialKind>(new()
        {
            [CredentialKind.AnthropicOauth] = "anthropic_oauth",
            [CredentialKind.AnthropicApiKey] = "anthropic_api_key",
            [CredentialKind.OpenAiOauth] = "openai_oauth",
            [CredentialKind.OpenAiApiKey] = "openai_api_key",
            [CredentialKind.GoogleApiKey] = "google_api_key",
            [CredentialKind.XaiApiKey] = "xai_api_key",
            [CredentialKind.OpenRouterKey] = "openrouter_key",
            [CredentialKind.CustomOpenAiCompatible] = "custom_openai_compatible",
        });

        AssertSpelling<CredentialScope>(new()
        {
            [CredentialScope.Personal] = "personal",
            [CredentialScope.SharedPool] = "shared_pool",
        });

        AssertSpelling<CredentialStatus>(new()
        {
            [CredentialStatus.Active] = "active",
            [CredentialStatus.Exhausted] = "exhausted",
            [CredentialStatus.Invalid] = "invalid",
            [CredentialStatus.Revoked] = "revoked",
        });
    }

    [Fact]
    public void BudgetEnumsMatchTheSpecification()
    {
        AssertSpelling<BudgetScopeType>(new()
        {
            [BudgetScopeType.Org] = "org",
            [BudgetScopeType.Team] = "team",
            [BudgetScopeType.Repo] = "repo",
            [BudgetScopeType.Project] = "project",
            [BudgetScopeType.User] = "user",
            [BudgetScopeType.Role] = "role",
            [BudgetScopeType.Tag] = "tag",
        });

        AssertSpelling<BudgetPeriod>(new()
        {
            [BudgetPeriod.Daily] = "daily",
            [BudgetPeriod.Weekly] = "weekly",
            [BudgetPeriod.Monthly] = "monthly",
            [BudgetPeriod.Quarterly] = "quarterly",
            [BudgetPeriod.Rolling30Days] = "rolling_30d",
            [BudgetPeriod.FiscalYear] = "fiscal_year",
            [BudgetPeriod.OneOff] = "one_off",
        });

        AssertSpelling<BudgetBehaviour>(new()
        {
            [BudgetBehaviour.Warn] = "warn",
            [BudgetBehaviour.RequireApproval] = "require_approval",
            [BudgetBehaviour.DowngradeModel] = "downgrade_model",
            [BudgetBehaviour.QueueUntilReset] = "queue_until_reset",
            [BudgetBehaviour.Block] = "block",
        });

        AssertSpelling<BudgetRollover>(new()
        {
            [BudgetRollover.None] = "none",
            [BudgetRollover.Full] = "full",
            [BudgetRollover.Capped] = "capped",
        });
    }

    [Fact]
    public void VerificationArtifactEnumsMatchTheSpecification()
    {
        AssertSpelling<VerificationArtifactKind>(new()
        {
            [VerificationArtifactKind.HostedPreview] = "hosted_preview",
            [VerificationArtifactKind.BuildArtifact] = "build_artifact",
            [VerificationArtifactKind.DistributionChannel] = "distribution_channel",
            [VerificationArtifactKind.Capture] = "capture",
            [VerificationArtifactKind.EphemeralInstance] = "ephemeral_instance",
            [VerificationArtifactKind.TestReport] = "test_report",
            [VerificationArtifactKind.HilReport] = "hil_report",
            [VerificationArtifactKind.None] = "none",
        });

        AssertSpelling<VerificationArtifactAudience>(new()
        {
            [VerificationArtifactAudience.Requester] = "requester",
            [VerificationArtifactAudience.EngineerOnly] = "engineer_only",
        });
    }

    [Fact]
    public void ProjectTypeMatchesTheStandardsSchema()
        => AssertSpelling<ProjectType>(new()
        {
            [ProjectType.Web] = "web",
            [ProjectType.Api] = "api",
            [ProjectType.MobileIos] = "mobile_ios",
            [ProjectType.MobileExpo] = "mobile_expo",
            [ProjectType.DesktopWin] = "desktop_win",
            [ProjectType.DesktopMac] = "desktop_mac",
            [ProjectType.Maui] = "maui",
            [ProjectType.Unity] = "unity",
            [ProjectType.GameServer] = "game_server",
            [ProjectType.Embedded] = "embedded",
            [ProjectType.Library] = "library",
        });

    [Fact]
    public void JobStatusMatchesTheStringsInTheQueueSql()
    {
        AssertSpelling<JobStatus>(new()
        {
            [JobStatus.Pending] = "pending",
            [JobStatus.Claimed] = "claimed",
            [JobStatus.Completed] = "completed",
            [JobStatus.Failed] = "failed",
            [JobStatus.Cancelled] = "cancelled",
        });

        // JobQueue writes these literals into its raw SQL, so a rename that broke the queue would
        // otherwise only surface at runtime.
        Assert.Contains($"status = '{EnumDbNames<JobStatus>.ToDb(JobStatus.Pending)}'", JobQueue.ClaimSql, StringComparison.Ordinal);
        Assert.Contains($"'{EnumDbNames<JobStatus>.ToDb(JobStatus.Claimed)}'", JobQueue.ClaimSql, StringComparison.Ordinal);
        Assert.Contains($"'{EnumDbNames<JobStatus>.ToDb(JobStatus.Completed)}'", JobQueue.CompleteSql, StringComparison.Ordinal);
        Assert.Contains($"'{EnumDbNames<JobStatus>.ToDb(JobStatus.Failed)}'", JobQueue.FailSql, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionAndRequestStatusesCoverTheStateMachine()
    {
        // Section 6 draws one machine. The request thread carries the whole of it (section 11);
        // a session carries the half that describes an agent run, plus handed_off from section 7.5.
        Assert.Equal("needs_input", EnumDbNames<RequestStatus>.ToDb(RequestStatus.NeedsInput));
        Assert.Equal("spec_ready", EnumDbNames<RequestStatus>.ToDb(RequestStatus.SpecReady));
        Assert.Equal("pr_open", EnumDbNames<RequestStatus>.ToDb(RequestStatus.PrOpen));
        Assert.Equal("preview_ready", EnumDbNames<RequestStatus>.ToDb(RequestStatus.PreviewReady));
        Assert.Equal("in_review", EnumDbNames<RequestStatus>.ToDb(RequestStatus.InReview));

        Assert.Equal("handed_off", EnumDbNames<SessionStatus>.ToDb(SessionStatus.HandedOff));
        Assert.Equal("pr_open", EnumDbNames<SessionStatus>.ToDb(SessionStatus.PrOpen));
    }

    [Fact]
    public void MilestoneLabelsAreTheFourRequesterFacingSteps()
        => AssertSpelling<MilestoneLabel>(new()
        {
            [MilestoneLabel.UnderstandingSetup] = "understanding_setup",
            [MilestoneLabel.MakingChanges] = "making_changes",
            [MilestoneLabel.CheckingItWorks] = "checking_it_works",
            [MilestoneLabel.PuttingItTogether] = "putting_it_together",
        });

    [Fact]
    public void EveryValueRoundTrips()
    {
        AssertRoundTrip<OrganizationMode>();
        AssertRoundTrip<MemberRole>();
        AssertRoundTrip<MemberCapability>();
        AssertRoundTrip<TeachingLevel>();
        AssertRoundTrip<IdentityProviderKind>();
        AssertRoundTrip<RepoStatus>();
        AssertRoundTrip<RequestStatus>();
        AssertRoundTrip<SessionStatus>();
        AssertRoundTrip<RunnerKind>();
        AssertRoundTrip<MilestoneLabel>();
        AssertRoundTrip<PullRequestState>();
        AssertRoundTrip<DeploymentState>();
        AssertRoundTrip<VerificationArtifactKind>();
        AssertRoundTrip<VerificationArtifactState>();
        AssertRoundTrip<VerificationArtifactAudience>();
        AssertRoundTrip<LedgerCategory>();
        AssertRoundTrip<LedgerUnit>();
        AssertRoundTrip<LedgerState>();
        AssertRoundTrip<CredentialKind>();
        AssertRoundTrip<CredentialScope>();
        AssertRoundTrip<CredentialStatus>();
        AssertRoundTrip<BudgetScopeType>();
        AssertRoundTrip<BudgetPeriod>();
        AssertRoundTrip<BudgetBehaviour>();
        AssertRoundTrip<BudgetRollover>();
        AssertRoundTrip<ProjectType>();
        AssertRoundTrip<JobType>();
        AssertRoundTrip<JobStatus>();
    }

    [Fact]
    public void AnUnknownStoredValueIsRejectedRatherThanGuessed()
    {
        // A row written by a newer Charter must fail loudly here, not silently become the zero value.
        Assert.Throws<ArgumentOutOfRangeException>(() => EnumDbNames<RepoStatus>.FromDb("quarantined"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnumDbNames<JobStatus>.FromDb(string.Empty));
    }

    [Theory]
    [InlineData("Id", "id")]
    [InlineData("OrgId", "org_id")]
    [InlineData("GithubInstallationId", "github_installation_id")]
    [InlineData("PrimerMd", "primer_md")]
    [InlineData("MaxSessionsPerDayFromOthers", "max_sessions_per_day_from_others")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("URL", "url")]
    public void SnakeCaseIsIdempotentAndBoundaryAware(string input, string expected)
    {
        Assert.Equal(expected, DbNaming.ToSnakeCase(input));
        Assert.Equal(expected, DbNaming.ToSnakeCase(DbNaming.ToSnakeCase(input)));
    }

    private static void AssertSpelling<TEnum>(Dictionary<TEnum, string> expected)
        where TEnum : struct, Enum
    {
        foreach (var (value, spelling) in expected)
        {
            Assert.Equal(spelling, EnumDbNames<TEnum>.ToDb(value));
            Assert.Equal(value, EnumDbNames<TEnum>.FromDb(spelling));
        }

        // Every value is accounted for: a new one added without a spelling fails here.
        Assert.Equal(Enum.GetValues<TEnum>().Length, expected.Count);
    }

    private static void AssertRoundTrip<TEnum>()
        where TEnum : struct, Enum
    {
        var spellings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in Enum.GetValues<TEnum>())
        {
            var stored = EnumDbNames<TEnum>.ToDb(value);

            Assert.False(string.IsNullOrWhiteSpace(stored));
            Assert.Equal(stored, stored.ToLowerInvariant());
            Assert.True(spellings.Add(stored), $"{typeof(TEnum).Name} spells two values as '{stored}'.");
            Assert.Equal(value, EnumDbNames<TEnum>.FromDb(stored));
        }
    }
}
