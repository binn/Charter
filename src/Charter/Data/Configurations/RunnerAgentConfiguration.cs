using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

/// <summary>
/// The registered Charter Agents (section 33.3).
/// </summary>
/// <remarks>
/// <para>
/// Two columns here hold verifiers rather than values — <c>credential_hash</c> and
/// <c>pairing_token_hash</c> — and the schema is what makes that hard to get wrong later: neither is
/// unique-indexed, because a salted PBKDF2 hash is never looked up by value. The row is found by the
/// identifier the token carries in front of its secret, which is why the lookup index this table
/// needs is the primary key it already has.
/// </para>
/// <para>
/// The capability set is a <c>text[]</c> with a GIN index for the same reason <c>jobs</c> has one:
/// section 27.3 matching is array containment, and the C# matcher and the SQL filter must agree.
/// </para>
/// </remarks>
internal sealed class RunnerAgentConfiguration : IEntityTypeConfiguration<RunnerAgent>
{
    public void Configure(EntityTypeBuilder<RunnerAgent> builder)
    {
        builder.ToTable("runner_agents");
        builder.HasKey(agent => agent.Id);

        builder.Property(agent => agent.Id).ValueGeneratedNever();
        builder.Property(agent => agent.Name).HasMaxLength(200).IsRequired();
        builder.Property(agent => agent.Mode).HasEnumConversion();
        builder.Property(agent => agent.Status).HasEnumConversion();

        // A semantic version plus whatever a pre-release suffix carries.
        builder.Property(agent => agent.AgentVersion).HasMaxLength(60);
        builder.Property(agent => agent.ProtocolVersion).IsRequired();
        builder.Property(agent => agent.Concurrency).IsRequired();

        builder.PrimitiveCollection(agent => agent.Capabilities).IsRequired();

        // The agent's own hash of its capability set, so drift is spotted from a heartbeat rather
        // than by shipping the whole set every thirty seconds (section 32.2).
        builder.Property(agent => agent.CapabilitiesHash).HasMaxLength(128);

        // Verifiers, not secrets. ASP.NET Core's PBKDF2 format is 84 characters at v3 parameters;
        // the bound is generous so a later parameter change is not a migration.
        builder.Property(agent => agent.CredentialHash).HasMaxLength(400);
        builder.Property(agent => agent.PairingTokenHash).HasMaxLength(400);

        builder.Property(agent => agent.Os).HasMaxLength(40);
        builder.Property(agent => agent.Arch).HasMaxLength(40);
        builder.Property(agent => agent.Rid).HasMaxLength(60);
        builder.Property(agent => agent.Hostname).HasMaxLength(255);
        builder.Property(agent => agent.CpuCount).IsRequired();

        builder.Property(agent => agent.RevokedReason).HasMaxLength(500);
        builder.Property(agent => agent.CreatedAt).IsRequired();

        // Written by pairing, by every connect and heartbeat, and by revocation from the UI. Three
        // paths to one row is exactly what the concurrency token is for — and here it is also what
        // makes a pairing token single-use under a race.
        builder.Property(agent => agent.Version).IsConcurrencyToken();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(agent => agent.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(agent => new { agent.OrgId, agent.Name })
            .HasDatabaseName("ix_runner_agents_org_id_name");

        // The routing read: which agents are online, and what do they advertise (section 27.3).
        builder.HasIndex(agent => agent.Status).HasDatabaseName("ix_runner_agents_status");

        builder.HasIndex(agent => agent.Capabilities)
            .HasMethod("gin")
            .HasDatabaseName("ix_runner_agents_capabilities");

        // Outstanding invitations, for the settings page and for pruning tokens nobody spent.
        builder.HasIndex(agent => agent.PairingTokenExpiresAt)
            .HasFilter("pairing_token_hash IS NOT NULL")
            .HasDatabaseName("ix_runner_agents_pairing_token_expires_at");
    }
}
