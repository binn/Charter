using Charter.Data.Configurations;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Charter.Data;

/// <summary>
/// Charter's only persistence surface. Postgres is the sole external dependency (section 3), and it
/// holds every piece of orchestration state, because the container can restart mid-session and every
/// session must be resumable from the database alone (section 2.3).
/// </summary>
public sealed class CharterDbContext : DbContext
{
    /// <summary>
    /// EF's own bookkeeping table, named to match the rest of the schema rather than
    /// <c>__EFMigrationsHistory</c>.
    /// </summary>
    public const string MigrationsHistoryTable = "__charter_migrations_history";

    public CharterDbContext(DbContextOptions<CharterDbContext> options)
        : base(options)
    {
    }

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Member> Members => Set<Member>();

    public DbSet<Domain.Identity> Identities => Set<Domain.Identity>();

    public DbSet<Repo> Repos => Set<Repo>();

    public DbSet<RepoScope> RepoScopes => Set<RepoScope>();

    public DbSet<AutoDispatchPolicy> AutoDispatchPolicies => Set<AutoDispatchPolicy>();

    public DbSet<Request> Requests => Set<Request>();

    public DbSet<Spec> Specs => Set<Spec>();

    /// <summary>
    /// Refinement conversations. Section 2.3: no in-memory orchestration state, so the conversation a
    /// requester is halfway through survives the container restarting under it.
    /// </summary>
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();

    public DbSet<ConversationTurnRecord> ConversationTurns => Set<ConversationTurnRecord>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Milestone> Milestones => Set<Milestone>();

    public DbSet<PullRequest> PullRequests => Set<PullRequest>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<VerificationArtifact> VerificationArtifacts => Set<VerificationArtifact>();

    public DbSet<Walkthrough> Walkthroughs => Set<Walkthrough>();

    public DbSet<Recap> Recaps => Set<Recap>();

    public DbSet<ConceptLedger> ConceptLedger => Set<ConceptLedger>();

    public DbSet<CredentialGrant> CredentialGrants => Set<CredentialGrant>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<Budget> Budgets => Set<Budget>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Job> Jobs => Set<Job>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceConcurrencyTokens();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        AdvanceConcurrencyTokens();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Listed rather than discovered by assembly scan: the control plane is worked on by several
        // hands at once, and a stray IEntityTypeConfiguration elsewhere in the assembly should not
        // silently join the model.
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new MemberConfiguration());
        modelBuilder.ApplyConfiguration(new IdentityConfiguration());
        modelBuilder.ApplyConfiguration(new RepoConfiguration());
        modelBuilder.ApplyConfiguration(new RepoScopeConfiguration());
        modelBuilder.ApplyConfiguration(new AutoDispatchPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new RequestConfiguration());
        modelBuilder.ApplyConfiguration(new SpecConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationTurnConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new EventConfiguration());
        modelBuilder.ApplyConfiguration(new MilestoneConfiguration());
        modelBuilder.ApplyConfiguration(new PullRequestConfiguration());
        modelBuilder.ApplyConfiguration(new DeploymentConfiguration());
        modelBuilder.ApplyConfiguration(new VerificationArtifactConfiguration());
        modelBuilder.ApplyConfiguration(new WalkthroughConfiguration());
        modelBuilder.ApplyConfiguration(new RecapConfiguration());
        modelBuilder.ApplyConfiguration(new ConceptLedgerConfiguration());
        modelBuilder.ApplyConfiguration(new CredentialGrantConfiguration());
        modelBuilder.ApplyConfiguration(new LedgerEntryConfiguration());
        modelBuilder.ApplyConfiguration(new BudgetConfiguration());
        modelBuilder.ApplyConfiguration(new AuditLogConfiguration());
        modelBuilder.ApplyConfiguration(new JobConfiguration());

        ApplySnakeCaseNames(modelBuilder);
    }

    /// <summary>
    /// Rewrites every generated identifier to <c>snake_case</c> after the configurations have run.
    /// </summary>
    /// <remarks>
    /// The transformation is idempotent, so the names spelled out explicitly in the configurations —
    /// table names, index names, the check constraint — pass through unchanged, and the ones EF
    /// derives from CLR names get folded. Postgres lower-cases unquoted identifiers anyway; doing it
    /// here means the model, a migration and a psql session all spell things the same way.
    /// </remarks>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);

            foreach (var property in entityType.GetProperties())
            {
                var column = table is null
                    ? property.Name
                    : property.GetColumnName(table.Value) ?? property.Name;

                property.SetColumnName(DbNaming.ToSnakeCase(column));
            }

            foreach (var key in entityType.GetKeys())
            {
                key.SetName(DbNaming.ToSnakeCase(key.GetName() ?? string.Empty));
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                foreignKey.SetConstraintName(DbNaming.ToSnakeCase(foreignKey.GetConstraintName() ?? string.Empty));
            }

            foreach (var index in entityType.GetIndexes())
            {
                index.SetDatabaseName(DbNaming.ToSnakeCase(index.GetDatabaseName() ?? string.Empty));
            }
        }
    }

    /// <summary>
    /// Advances the optimistic concurrency token on rows written by more than one path.
    /// </summary>
    /// <remarks>
    /// Npgsql 10 removed <c>UseXminAsConcurrencyToken</c>, and a system column cannot be created by
    /// a migration, so the token is an ordinary integer column. EF still compares the original value
    /// in the <c>WHERE</c> clause, so incrementing the current value here is what makes a concurrent
    /// write fail loudly instead of silently winning.
    /// </remarks>
    private void AdvanceConcurrencyTokens()
    {
        foreach (var entry in ChangeTracker.Entries<IVersionedEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.NextVersion();
            }
        }
    }
}
