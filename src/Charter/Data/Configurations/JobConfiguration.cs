using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

/// <summary>
/// The queue table (sections 2.3, 33.4). Every index here exists to serve one of exactly three
/// statements in <see cref="JobQueue"/>: claim, release expired leases, and release a shutting-down
/// worker's claims.
/// </summary>
internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.Type).HasEnumConversion();
        builder.Property(job => job.Payload).IsJsonb().IsRequired();
        builder.Property(job => job.Status).HasEnumConversion();
        builder.Property(job => job.AvailableAt).IsRequired();
        builder.Property(job => job.ClaimedBy).HasMaxLength(120);
        builder.Property(job => job.Attempts).IsRequired();
        builder.Property(job => job.MaxAttempts).IsRequired();
        builder.Property(job => job.Priority).IsRequired();
        builder.Property(job => job.CreatedAt).IsRequired();
        builder.Property(job => job.UpdatedAt).IsRequired();

        // Claimed by one worker, released by lease expiry, completed by another code path.
        builder.Property(job => job.Version).IsConcurrencyToken();

        builder.PrimitiveCollection(job => job.RequiredCapabilities).IsRequired();

        // The claim query, and the reason this table has no foreign keys: nothing about claiming a
        // job should need to touch another table. Partial, because a healthy queue is mostly rows
        // that are already done, and the index should only be as large as the backlog.
        builder.HasIndex(job => new { job.Status, job.Priority, job.AvailableAt })
            .IsDescending(false, true, false)
            .HasFilter("status = 'pending'")
            .HasDatabaseName("ix_jobs_claimable");

        // Lease expiry sweep (section 33.4): a crashed worker's jobs return to the queue.
        builder.HasIndex(job => job.LeaseExpiresAt)
            .HasFilter("status = 'claimed'")
            .HasDatabaseName("ix_jobs_lease_expires_at");

        // Graceful shutdown (section 31) releases everything this instance still holds.
        builder.HasIndex(job => job.ClaimedBy)
            .HasFilter("status = 'claimed'")
            .HasDatabaseName("ix_jobs_claimed_by");

        // Capability matching (section 27.3) is a Postgres array containment test, which needs GIN.
        builder.HasIndex(job => job.RequiredCapabilities)
            .HasMethod("gin")
            .HasDatabaseName("ix_jobs_required_capabilities");
    }
}
