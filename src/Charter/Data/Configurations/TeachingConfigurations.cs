using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class WalkthroughConfiguration : IEntityTypeConfiguration<Walkthrough>
{
    public void Configure(EntityTypeBuilder<Walkthrough> builder)
    {
        builder.ToTable("walkthroughs");
        builder.HasKey(walkthrough => walkthrough.Id);

        builder.Property(walkthrough => walkthrough.Id).ValueGeneratedNever();
        builder.Property(walkthrough => walkthrough.Level).HasEnumConversion();
        builder.Property(walkthrough => walkthrough.BodyMd).IsRequired();
        builder.Property(walkthrough => walkthrough.CostUsd).IsMoney();
        builder.Property(walkthrough => walkthrough.GeneratedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(walkthrough => walkthrough.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One rendering per level: the per-walkthrough "more detail" override regenerates at another
        // level rather than overwriting the one the user already read (section 13).
        builder.HasIndex(walkthrough => new { walkthrough.SessionId, walkthrough.Level })
            .IsUnique()
            .HasDatabaseName("ux_walkthroughs_session_id_level");
    }
}

// Fully qualified: `Charter.Recap` is a namespace as well as `Charter.Domain.Recap` being a type.
internal sealed class RecapConfiguration : IEntityTypeConfiguration<Charter.Domain.Recap>
{
    public void Configure(EntityTypeBuilder<Charter.Domain.Recap> builder)
    {
        builder.ToTable("recaps");
        builder.HasKey(recap => recap.Id);

        builder.Property(recap => recap.Id).ValueGeneratedNever();
        builder.Property(recap => recap.BodyMd).IsRequired();
        builder.Property(recap => recap.RiskItems).IsJsonb().IsRequired();
        builder.Property(recap => recap.CostUsd).IsMoney();
        builder.Property(recap => recap.GeneratedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(recap => recap.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(recap => recap.SessionId)
            .IsUnique()
            .HasDatabaseName("ux_recaps_session_id");
    }
}

internal sealed class ConceptLedgerConfiguration : IEntityTypeConfiguration<ConceptLedger>
{
    public void Configure(EntityTypeBuilder<ConceptLedger> builder)
    {
        // Singular, because it is one ledger per person rather than a bag of rows.
        builder.ToTable("concept_ledger");
        builder.HasKey(concept => concept.Id);

        builder.Property(concept => concept.Id).ValueGeneratedNever();
        builder.Property(concept => concept.Concept).HasMaxLength(120).IsRequired();
        builder.Property(concept => concept.FirstExplainedAt).IsRequired();
        builder.Property(concept => concept.LastReferencedAt).IsRequired();
        builder.Property(concept => concept.TimesReferenced).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(concept => concept.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(concept => new { concept.UserId, concept.Concept })
            .IsUnique()
            .HasDatabaseName("ux_concept_ledger_user_id_concept");

        // Injection is capped at the most-recent few dozen concepts (section 13).
        builder.HasIndex(concept => new { concept.UserId, concept.LastReferencedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_concept_ledger_user_id_last_referenced_at");
    }
}

internal sealed class ExplainThisUsageConfiguration : IEntityTypeConfiguration<ExplainThisUsage>
{
    public void Configure(EntityTypeBuilder<ExplainThisUsage> builder)
    {
        builder.ToTable("explain_this_usage");

        // The pair is the identity, so there is no surrogate key: the primary key is what makes the
        // increment a single atomic upsert rather than a read followed by a write.
        builder.HasKey(usage => new { usage.UserId, usage.Day });

        // `date`, not a timestamp: the window is a whole UTC day, and storing an instant would
        // invite a comparison that lands halfway through one.
        builder.Property(usage => usage.Day).HasColumnType("date").IsRequired();
        builder.Property(usage => usage.Used).IsRequired();
        builder.Property(usage => usage.LastUsedAt).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(usage => usage.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Pruning spent days sweeps by age.
        builder.HasIndex(usage => usage.Day).HasDatabaseName("ix_explain_this_usage_day");
    }
}
