using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class CredentialGrantConfiguration : IEntityTypeConfiguration<CredentialGrant>
{
    public void Configure(EntityTypeBuilder<CredentialGrant> builder)
    {
        builder.ToTable("credential_grants");
        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.Id).ValueGeneratedNever();
        builder.Property(grant => grant.Kind).HasEnumConversion();
        builder.Property(grant => grant.BaseUrl).HasMaxLength(500);
        builder.Property(grant => grant.Scope).HasEnumConversion();

        // Ciphertext, encrypted with CHARTER_CREDENTIAL_KEY (section 20b.2). There is no plaintext
        // column and no plaintext property anywhere on this type, and no read model exposes these.
        builder.Property(grant => grant.SecretEncrypted).IsRequired();
        builder.Property(grant => grant.RefreshTokenEncrypted);

        builder.Property(grant => grant.Status).HasEnumConversion();

        // Nullable, and the null means "exhausted, with no idea when it comes back" rather than a
        // far-future sentinel. Section 20b.3 shows this instant to a requester as "waiting for
        // capacity", and a sentinel would render as the year 9999.
        builder.Property(grant => grant.ExhaustedUntil);

        // Why a credential died, so an admin does not have to go to the logs (section 20b.2 shows
        // provider, owner, status, last used - a dead one needs the reason too). Never a token: the
        // store is handed prose, and CredentialGrant truncates it.
        builder.Property(grant => grant.InvalidReason).HasMaxLength(CredentialGrant.MaxInvalidReasonLength);

        // Tier 2 of section 20b.3. Tracked apart from the subscription because the two run out
        // separately, so one status column would make the resolver guess which allowance a 429 hit.
        builder.Property(grant => grant.OverflowEnabled).IsRequired();
        builder.Property(grant => grant.OverflowStatus).HasEnumConversion();
        builder.Property(grant => grant.OverflowExhaustedUntil);

        builder.Property(grant => grant.Priority).IsRequired();
        builder.Property(grant => grant.CreatedAt).IsRequired();

        builder.Ignore(grant => grant.IsExhaustedIndefinitely);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(grant => grant.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(grant => grant.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The resolution chain of section 20b.3 walks the org's pool by priority, skipping anything
        // exhausted or invalid.
        builder.HasIndex(grant => new { grant.OrgId, grant.Status, grant.Priority })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_credential_grants_org_id_status_priority");

        builder.HasIndex(grant => grant.OwnerUserId).HasDatabaseName("ix_credential_grants_owner_user_id");
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.Category).HasEnumConversion();
        builder.Property(entry => entry.Unit).HasEnumConversion();

        // Both units, always (sections 20b.5, 34.1). Reporting a subscription session as $0.00 makes
        // budget dashboards lie, so imputed_usd is never null and is populated for metered spend too.
        builder.Property(entry => entry.Usd).IsMoney();
        builder.Property(entry => entry.QuotaSessions).IsMoney();
        builder.Property(entry => entry.ImputedUsd).IsMoney();
        builder.Property(entry => entry.State).HasEnumConversion();
        builder.Property(entry => entry.CreatedAt).IsRequired();

        builder.PrimitiveCollection(entry => entry.BudgetIds).IsRequired();

        // Derived from Unit; storing it would let the denominating amount drift from the two columns
        // it is meant to summarise.
        builder.Ignore(entry => entry.Amount);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Money outlives the work it paid for: deleting a session keeps the accounting line and
        // detaches it.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(entry => entry.SessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<CredentialGrant>()
            .WithMany()
            .HasForeignKey(entry => entry.CredentialGrantId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(entry => new { entry.OrgId, entry.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ledger_entries_org_id_created_at");

        builder.HasIndex(entry => new { entry.UserId, entry.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ledger_entries_user_id_created_at");

        builder.HasIndex(entry => entry.SessionId).HasDatabaseName("ix_ledger_entries_session_id");

        // Reservations expire on a TTL so a crashed orchestrator does not strand budget (34.4). This
        // partial index is what the sweep reads.
        builder.HasIndex(entry => entry.ReservedUntil)
            .HasFilter("state = 'reserved'")
            .HasDatabaseName("ix_ledger_entries_reserved_until");

        builder.HasIndex(entry => entry.BudgetIds)
            .HasMethod("gin")
            .HasDatabaseName("ix_ledger_entries_budget_ids");
    }
}

internal sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(budget => budget.Id);

        builder.Property(budget => budget.Id).ValueGeneratedNever();
        builder.Property(budget => budget.Name).HasMaxLength(200).IsRequired();
        builder.Property(budget => budget.ScopeType).HasEnumConversion();

        // Text, because a role scope holds a role name and a tag scope holds a tag; section 34.2
        // spells it as a single scope_id across all seven scope types.
        builder.Property(budget => budget.ScopeId).HasMaxLength(200);
        builder.Property(budget => budget.Unit).HasEnumConversion();
        builder.Property(budget => budget.Period).HasEnumConversion();
        builder.Property(budget => budget.Amount).IsMoney();
        builder.Property(budget => budget.Behaviour).HasEnumConversion();
        builder.Property(budget => budget.ApprovalThreshold).IsMoney();
        builder.Property(budget => budget.Rollover).HasEnumConversion();
        builder.Property(budget => budget.RolloverCap).IsMoney();
        builder.Property(budget => budget.ReservedAmount).IsMoney();
        builder.Property(budget => budget.CreatedAt).IsRequired();
        builder.Property(budget => budget.UpdatedAt).IsRequired();

        builder.PrimitiveCollection(budget => budget.Categories)
            .HasEnumElements<IReadOnlyList<LedgerCategory>, LedgerCategory>()
            .IsRequired();

        builder.PrimitiveCollection(budget => budget.AlertThresholds).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(budget => budget.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // Section 34.3 evaluates every applicable budget together, so the query that matters loads
        // the whole candidate set for an org and filters in memory.
        builder.HasIndex(budget => new { budget.OrgId, budget.ScopeType, budget.ScopeId })
            .HasDatabaseName("ix_budgets_org_id_scope_type_scope_id");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.Action).HasMaxLength(120).IsRequired();
        builder.Property(entry => entry.TargetType).HasMaxLength(80).IsRequired();
        builder.Property(entry => entry.TargetId).HasMaxLength(120);
        builder.Property(entry => entry.Metadata).IsOptionalJsonb();
        builder.Property(entry => entry.CreatedAt).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The audit trail survives the person: an entry naming a deleted user keeps the action.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(entry => new { entry.OrgId, entry.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_logs_org_id_created_at");

        builder.HasIndex(entry => new { entry.TargetType, entry.TargetId })
            .HasDatabaseName("ix_audit_logs_target_type_target_id");
    }
}
