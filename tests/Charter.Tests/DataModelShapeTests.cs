using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Charter.Tests;

/// <summary>
/// Asserts the shape of the mapped model: table and column names, storage types, indexes and
/// delete behaviours.
/// </summary>
/// <remarks>
/// None of this needs a database. EF builds the model from the provider's type mappings, so a
/// connection string that points nowhere is enough to check that <c>cost_usd</c> is
/// <c>numeric</c> and not <c>double precision</c>, that every enum is stored as text, and that
/// deleting a session does not take the accounting with it.
/// </remarks>
public class DataModelShapeTests : IDisposable
{
    private const string UnusedConnectionString =
        "Host=localhost;Port=5432;Database=charter;Username=charter;Password=unused";

    private readonly CharterDbContext _db;

    /// <summary>
    /// The design-time model rather than <c>DbContext.Model</c>: the runtime model is read-optimised
    /// and drops the configuration these assertions are about, such as check constraints and index
    /// sort order.
    /// </summary>
    private readonly IModel _model;

    public DataModelShapeTests()
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, UnusedConnectionString);

        _db = new CharterDbContext(options.Options);
        _model = _db.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>Every entity in section 5, plus the four the later sections add.</summary>
    private static readonly (Type ClrType, string Table)[] MappedEntities =
    [
        (typeof(Organization), "organizations"),
        (typeof(User), "users"),
        (typeof(Member), "members"),
        (typeof(Charter.Domain.Identity), "identities"),
        (typeof(Repo), "repos"),
        (typeof(RepoScope), "repo_scopes"),
        (typeof(AutoDispatchPolicy), "auto_dispatch_policies"),
        (typeof(Request), "requests"),
        (typeof(RequestFeedback), "request_feedback"),
        (typeof(Spec), "specs"),
        (typeof(ConversationRecord), "conversations"),
        (typeof(ConversationTurnRecord), "conversation_turns"),
        (typeof(Session), "sessions"),
        (typeof(Event), "events"),
        (typeof(Milestone), "milestones"),
        (typeof(PullRequest), "pull_requests"),
        (typeof(Deployment), "deployments"),
        (typeof(VerificationArtifact), "verification_artifacts"),
        (typeof(Walkthrough), "walkthroughs"),
        (typeof(Recap), "recaps"),
        (typeof(ConceptLedger), "concept_ledger"),
        (typeof(CredentialGrant), "credential_grants"),
        (typeof(LedgerEntry), "ledger_entries"),
        (typeof(Budget), "budgets"),
        (typeof(AuditLog), "audit_logs"),
        (typeof(Job), "jobs"),
    ];

    public static TheoryData<Type, string> ExpectedTableNames
    {
        get
        {
            var data = new TheoryData<Type, string>();

            foreach (var (clrType, table) in MappedEntities)
            {
                data.Add(clrType, table);
            }

            return data;
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(ExpectedTableNames))]
    public void EveryEntityInSectionFiveIsMapped(Type clrType, string table)
    {
        var entity = _model.FindEntityType(clrType);

        Assert.NotNull(entity);
        Assert.Equal(table, entity.GetTableName());
    }

    [Fact]
    public void TheModelHasNoEntitiesBeyondTheOnesDeclaredHere()
    {
        var mapped = _model.GetEntityTypes().Select(entity => entity.ClrType).ToHashSet();
        var expected = MappedEntities.Select(entity => entity.ClrType).ToHashSet();

        Assert.Equal(expected.Count, mapped.Count);
        Assert.All(mapped, type => Assert.Contains(type, expected));
    }

    [Fact]
    public void EveryColumnIsSnakeCase()
    {
        foreach (var entity in _model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            Assert.NotNull(table);

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(table.Value);

                Assert.NotNull(column);
                Assert.Equal(column, column.ToLowerInvariant());
                Assert.Equal(column, DbNaming.ToSnakeCase(column));
            }
        }
    }

    [Fact]
    public void EveryKeyIndexAndForeignKeyIsSnakeCase()
    {
        foreach (var entity in _model.GetEntityTypes())
        {
            foreach (var key in entity.GetKeys())
            {
                Assert.StartsWith("pk_", key.GetName(), StringComparison.Ordinal);
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var name = foreignKey.GetConstraintName();
                Assert.NotNull(name);
                Assert.Equal(name, name.ToLowerInvariant());
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                Assert.NotNull(name);
                Assert.Equal(name, name.ToLowerInvariant());
                Assert.True(
                    name.StartsWith("ix_", StringComparison.Ordinal) || name.StartsWith("ux_", StringComparison.Ordinal),
                    $"Index '{name}' should be prefixed ix_ or ux_.");
            }
        }
    }

    [Fact]
    public void EveryEnumIsStoredAsTextRatherThanAnInteger()
    {
        // An integer enum makes a migration a landmine and makes raw SQL unreadable.
        foreach (var entity in _model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!clrType.IsEnum)
                {
                    continue;
                }

                var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;

                Assert.NotNull(converter);
                Assert.Equal(typeof(string), converter.ProviderClrType);
            }
        }
    }

    [Fact]
    public void EnumArraysAreStoredAsTextArrays()
    {
        Assert.Equal("text[]", ColumnType<Member>(nameof(Member.Roles)));
        Assert.Equal("text[]", ColumnType<Member>(nameof(Member.Capabilities)));
        Assert.Equal("text[]", ColumnType<Budget>(nameof(Budget.Categories)));
        Assert.Equal("text[]", ColumnType<AutoDispatchPolicy>(nameof(AutoDispatchPolicy.ProjectTypes)));
        Assert.Equal("text[]", ColumnType<AutoDispatchPolicy>(nameof(AutoDispatchPolicy.AllowedPaths)));
        Assert.Equal("text[]", ColumnType<Job>(nameof(Job.RequiredCapabilities)));
        Assert.Equal("uuid[]", ColumnType<LedgerEntry>(nameof(LedgerEntry.BudgetIds)));
        Assert.Equal("double precision[]", ColumnType<Budget>(nameof(Budget.AlertThresholds)));
    }

    [Fact]
    public void EveryTimestampIsTimestamptz()
    {
        foreach (var entity in _model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (clrType == typeof(DateTimeOffset))
                {
                    Assert.Equal("timestamp with time zone", property.GetColumnType());
                }

                // DateTime would store an ambiguous local instant. There should not be any.
                Assert.NotEqual(typeof(DateTime), clrType);
            }
        }
    }

    [Fact]
    public void JsonColumnsAreJsonb()
    {
        Assert.Equal("jsonb", ColumnType<Event>(nameof(Event.Payload)));
        Assert.Equal("jsonb", ColumnType<Job>(nameof(Job.Payload)));
        Assert.Equal("jsonb", ColumnType<Spec>(nameof(Spec.AcceptanceCriteria)));
        Assert.Equal("jsonb", ColumnType<Spec>(nameof(Spec.Scope)));
        Assert.Equal("jsonb", ColumnType<Repo>(nameof(Repo.CharterConfigSnapshot)));

        // Section 27.7's kind-specific body: checksums, sizes, capture lists, device data.
        Assert.Equal("jsonb", ColumnType<VerificationArtifact>(nameof(VerificationArtifact.Payload)));
        Assert.Equal("jsonb", ColumnType<Recap>(nameof(Recap.RiskItems)));
        Assert.Equal("jsonb", ColumnType<AuditLog>(nameof(AuditLog.Metadata)));
    }

    [Fact]
    public void MoneyIsNumericNeverFloatingPoint()
    {
        Assert.Equal("numeric(14,4)", ColumnType<Session>(nameof(Session.CostUsd)));
        Assert.Equal("numeric(14,4)", ColumnType<LedgerEntry>(nameof(LedgerEntry.Usd)));
        Assert.Equal("numeric(14,4)", ColumnType<LedgerEntry>(nameof(LedgerEntry.QuotaSessions)));
        Assert.Equal("numeric(14,4)", ColumnType<LedgerEntry>(nameof(LedgerEntry.ImputedUsd)));
        Assert.Equal("numeric(14,4)", ColumnType<Budget>(nameof(Budget.Amount)));
    }

    [Fact]
    public void CredentialCiphertextIsBytesAndNoPlaintextColumnExists()
    {
        var entity = _model.FindEntityType(typeof(CredentialGrant));
        Assert.NotNull(entity);

        Assert.Equal("bytea", ColumnType<CredentialGrant>(nameof(CredentialGrant.SecretEncrypted)));
        Assert.Equal("bytea", ColumnType<CredentialGrant>(nameof(CredentialGrant.RefreshTokenEncrypted)));

        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
        var columns = entity.GetProperties().Select(property => property.GetColumnName(table)).ToArray();

        Assert.Contains("secret_encrypted", columns);
        Assert.Contains("refresh_token_encrypted", columns);
        Assert.DoesNotContain("secret", columns);
        Assert.DoesNotContain("refresh_token", columns);
        Assert.DoesNotContain("api_key", columns);
    }

    [Fact]
    public void TheEventTableIsIndexedForCursorPaginationBySession()
    {
        var entity = _model.FindEntityType(typeof(Event));
        Assert.NotNull(entity);

        var index = entity.GetIndexes().Single(candidate => candidate.GetDatabaseName() == "ux_events_session_id_seq");

        Assert.True(index.IsUnique);
        Assert.Equal(["SessionId", "Seq"], index.Properties.Select(property => property.Name));

        // Retention pruning sweeps by age (section 20).
        Assert.Contains(entity.GetIndexes(), candidate => candidate.GetDatabaseName() == "ix_events_created_at");
    }

    [Fact]
    public void TheQueueIsIndexedForClaimingAndForLeaseExpiry()
    {
        var entity = _model.FindEntityType(typeof(Job));
        Assert.NotNull(entity);

        var claim = entity.GetIndexes().Single(index => index.GetDatabaseName() == "ix_jobs_claimable");

        Assert.Equal(["Status", "Priority", "AvailableAt"], claim.Properties.Select(property => property.Name));
        Assert.Equal(new[] { false, true, false }, claim.IsDescending!);
        Assert.Equal("status = 'pending'", claim.GetFilter());

        var lease = entity.GetIndexes().Single(index => index.GetDatabaseName() == "ix_jobs_lease_expires_at");
        Assert.Equal("status = 'claimed'", lease.GetFilter());

        var capabilities = entity.GetIndexes().Single(index => index.GetDatabaseName() == "ix_jobs_required_capabilities");
        Assert.Equal("gin", capabilities.GetMethod());

        // No foreign keys: claiming a job must never need another table.
        Assert.Empty(entity.GetForeignKeys());
    }

    [Fact]
    public void SessionAndJobCarryConcurrencyTokens()
    {
        // Both rows are written by more than one path.
        Assert.True(Property<Session>(nameof(Session.Version)).IsConcurrencyToken);
        Assert.True(Property<Job>(nameof(Job.Version)).IsConcurrencyToken);
    }

    [Fact]
    public void UniquenessIsEnforcedWhereTheDomainDependsOnIt()
    {
        AssertUniqueIndex<User>("ux_users_email");
        AssertUniqueIndex<Member>("ux_members_org_id_user_id");
        AssertUniqueIndex<Charter.Domain.Identity>("ux_identities_provider_provider_user_id");
        AssertUniqueIndex<Repo>("ux_repos_org_id_full_name");
        AssertUniqueIndex<Spec>("ux_specs_request_id_version");
        AssertUniqueIndex<Event>("ux_events_session_id_seq");
        AssertUniqueIndex<PullRequest>("ux_pull_requests_session_id_number");
        AssertUniqueIndex<Recap>("ux_recaps_session_id");
        AssertUniqueIndex<Walkthrough>("ux_walkthroughs_session_id_level");
        AssertUniqueIndex<ConceptLedger>("ux_concept_ledger_user_id_concept");
        AssertUniqueIndex<ConversationTurnRecord>("ux_conversation_turns_conversation_id_seq");
    }

    [Fact]
    public void TheConversationTurnBodyIsAColumnAndNotAPublicMember()
    {
        // Section 16: persistence must not become the back door that hands a prompt builder raw
        // requester text. The characters map to a private property, and the type's only public
        // readers are AuthoredText - which throws on a requester turn - and a typed RequesterText.
        var entity = _model.FindEntityType(typeof(ConversationTurnRecord));
        Assert.NotNull(entity);

        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
        var columns = entity.GetProperties().Select(property => property.GetColumnName(table)).ToArray();

        Assert.Contains("body", columns);

        var body = entity.FindProperty("Body");
        Assert.NotNull(body);
        Assert.Null(typeof(ConversationTurnRecord).GetProperty("Body"));

        AssertNotMapped<ConversationTurnRecord>(nameof(ConversationTurnRecord.AuthoredText));
        AssertNotMapped<ConversationTurnRecord>(nameof(ConversationTurnRecord.RequesterText));
        AssertNotMapped<ConversationTurnRecord>(nameof(ConversationTurnRecord.IsUntrusted));
    }

    [Fact]
    public void AConversationOwnsItsTurnsAndOutlivesItsRequest()
    {
        AssertDelete<ConversationTurnRecord>(
            nameof(ConversationTurnRecord.ConversationId),
            DeleteBehavior.Cascade);
        AssertDelete<ConversationRecord>(nameof(ConversationRecord.OrgId), DeleteBehavior.Cascade);
        AssertDelete<ConversationRecord>(nameof(ConversationRecord.RequestId), DeleteBehavior.SetNull);
        AssertDelete<ConversationRecord>(nameof(ConversationRecord.ConfirmedBy), DeleteBehavior.SetNull);
    }

    [Fact]
    public void TheUserRowCarriesEveryPreferenceTheApiAccepts()
    {
        // Section 3.1: no browser storage, so a preference with no column is a PATCH that is accepted
        // and dropped. Section 12 and section 30.4 name all four.
        var entity = _model.FindEntityType(typeof(User));
        Assert.NotNull(entity);

        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
        var columns = entity.GetProperties().Select(property => property.GetColumnName(table)).ToArray();

        Assert.Contains("teaching_level", columns);
        Assert.Contains("theme", columns);
        Assert.Contains("pane", columns);
        Assert.Contains("requester_onboarding_completed_at", columns);

        // Section 12 defaults the pane by role, which is only applicable while nobody has chosen.
        Assert.True(Property<User>(nameof(User.Pane)).IsNullable);
        Assert.False(Property<User>(nameof(User.Theme)).IsNullable);
        Assert.True(Property<User>(nameof(User.RequesterOnboardingCompletedAt)).IsNullable);
    }

    [Fact]
    public void ThePullRequestRowCarriesTheHeadBranchTheEngineerDetailsShow()
    {
        // Section 27.7: the `Details` disclosure names the branch alongside the PR number and SHA.
        var entity = _model.FindEntityType(typeof(PullRequest));
        Assert.NotNull(entity);

        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
        var columns = entity.GetProperties().Select(property => property.GetColumnName(table)).ToArray();

        Assert.Contains("head_branch", columns);
        Assert.True(Property<PullRequest>(nameof(PullRequest.HeadBranch)).IsNullable);
    }

    [Fact]
    public void FeedbackIsARowAndOutlivesTheSessionItJudged()
    {
        // Section 11: one thread per request, forever, and several sessions collapse into it - so the
        // verdict is a row per round rather than a column that the next round overwrites.
        AssertDelete<RequestFeedback>(nameof(RequestFeedback.RequestId), DeleteBehavior.Cascade);
        AssertDelete<RequestFeedback>(nameof(RequestFeedback.SessionId), DeleteBehavior.SetNull);
        AssertDelete<RequestFeedback>(nameof(RequestFeedback.SubmittedBy), DeleteBehavior.Restrict);

        var entity = _model.FindEntityType(typeof(RequestFeedback));
        Assert.NotNull(entity);

        Assert.Contains(
            entity.GetIndexes(),
            index => index.GetDatabaseName() == "ix_request_feedback_request_id_created_at");
    }

    [Fact]
    public void TheCredentialGrantCarriesItsOverflowAndItsObituary()
    {
        // Tier 2 of section 20b.3 needs its own columns, or the tier can never fire; and "invalid"
        // without a reason sends an admin to the container logs.
        var entity = _model.FindEntityType(typeof(CredentialGrant));
        Assert.NotNull(entity);

        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
        var columns = entity.GetProperties().Select(property => property.GetColumnName(table)).ToArray();

        Assert.Contains("overflow_enabled", columns);
        Assert.Contains("overflow_status", columns);
        Assert.Contains("overflow_exhausted_until", columns);
        Assert.Contains("invalid_reason", columns);

        // Nullable, because "exhausted with no known reset" is a real state and a far-future sentinel
        // renders as the year 9999 wherever section 20b.3 shows "waiting for capacity".
        Assert.True(Property<CredentialGrant>(nameof(CredentialGrant.ExhaustedUntil)).IsNullable);
        Assert.True(Property<CredentialGrant>(nameof(CredentialGrant.OverflowExhaustedUntil)).IsNullable);
        Assert.False(Property<CredentialGrant>(nameof(CredentialGrant.OverflowEnabled)).IsNullable);

        AssertNotMapped<CredentialGrant>(nameof(CredentialGrant.IsExhaustedIndefinitely));
    }

    [Fact]
    public void OwnedRowsCascadeWithTheirOwner()
    {
        AssertDelete<Member>(nameof(Member.OrgId), DeleteBehavior.Cascade);
        AssertDelete<Repo>(nameof(Repo.OrgId), DeleteBehavior.Cascade);
        AssertDelete<Event>(nameof(Event.SessionId), DeleteBehavior.Cascade);
        AssertDelete<Milestone>(nameof(Milestone.EventId), DeleteBehavior.Cascade);
        AssertDelete<Session>(nameof(Session.SpecId), DeleteBehavior.Cascade);
        AssertDelete<Deployment>(nameof(Deployment.PullRequestId), DeleteBehavior.Cascade);
        AssertDelete<VerificationArtifact>(nameof(VerificationArtifact.SessionId), DeleteBehavior.Cascade);
    }

    [Fact]
    public void AccountingAndAttributionSurviveTheRowsTheyPointAt()
    {
        // Section 20 makes deletion first-class, and none of it may quietly erase the money trail.
        AssertDelete<LedgerEntry>(nameof(LedgerEntry.SessionId), DeleteBehavior.SetNull);
        AssertDelete<LedgerEntry>(nameof(LedgerEntry.CredentialGrantId), DeleteBehavior.SetNull);
        AssertDelete<LedgerEntry>(nameof(LedgerEntry.UserId), DeleteBehavior.Restrict);
        AssertDelete<AuditLog>(nameof(AuditLog.ActorUserId), DeleteBehavior.SetNull);
        AssertDelete<Request>(nameof(Request.RequesterId), DeleteBehavior.Restrict);
        AssertDelete<Spec>(nameof(Spec.ApprovedBy), DeleteBehavior.SetNull);
    }

    [Fact]
    public void DerivedPropertiesAreNotColumns()
    {
        AssertNotMapped<LedgerEntry>(nameof(LedgerEntry.Amount));
        AssertNotMapped<AutoDispatchPolicy>(nameof(AutoDispatchPolicy.Specificity));
        AssertNotMapped<Repo>(nameof(Repo.IsRequesterVisible));
        AssertNotMapped<Session>(nameof(Session.IsTerminal));
        AssertNotMapped<Spec>(nameof(Spec.IsApproved));
    }

    [Fact]
    public void TheRepoScopeCheckConstraintKeepsTheSubjectExclusive()
    {
        var entity = _model.FindEntityType(typeof(RepoScope));
        Assert.NotNull(entity);

        var constraint = Assert.Single(entity.GetCheckConstraints());

        Assert.Equal("ck_repo_scopes_member_xor_role", constraint.Name);
        Assert.Equal("(member_id IS NULL) <> (role IS NULL)", constraint.Sql);
    }

    [Fact]
    public void TheMigrationsHistoryTableIsNamedLikeTheRestOfTheSchema()
        => Assert.Equal("__charter_migrations_history", CharterDbContext.MigrationsHistoryTable);

    private void AssertNotMapped<TEntity>(string propertyName)
    {
        var entity = _model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        Assert.Null(entity.FindProperty(propertyName));
    }

    private IProperty Property<TEntity>(string propertyName)
    {
        var entity = _model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);

        var property = entity.FindProperty(propertyName);
        Assert.NotNull(property);

        return property;
    }

    private string? ColumnType<TEntity>(string propertyName) => Property<TEntity>(propertyName).GetColumnType();

    private void AssertUniqueIndex<TEntity>(string indexName)
    {
        var entity = _model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);

        var index = entity.GetIndexes().SingleOrDefault(candidate => candidate.GetDatabaseName() == indexName);

        Assert.NotNull(index);
        Assert.True(index.IsUnique, $"{indexName} should be unique.");
    }

    private void AssertDelete<TEntity>(string foreignKeyProperty, DeleteBehavior expected)
    {
        var entity = _model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);

        var foreignKey = entity.GetForeignKeys()
            .Single(candidate => candidate.Properties.Count == 1
                && candidate.Properties[0].Name == foreignKeyProperty);

        Assert.Equal(expected, foreignKey.DeleteBehavior);
    }
}
