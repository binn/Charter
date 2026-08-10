using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class PullRequestConfiguration : IEntityTypeConfiguration<PullRequest>
{
    public void Configure(EntityTypeBuilder<PullRequest> builder)
    {
        builder.ToTable("pull_requests");
        builder.HasKey(pullRequest => pullRequest.Id);

        builder.Property(pullRequest => pullRequest.Id).ValueGeneratedNever();
        builder.Property(pullRequest => pullRequest.Number).IsRequired();
        builder.Property(pullRequest => pullRequest.Url).HasMaxLength(500).IsRequired();
        builder.Property(pullRequest => pullRequest.HeadSha).HasMaxLength(64).IsRequired();
        builder.Property(pullRequest => pullRequest.State).HasEnumConversion();
        builder.Property(pullRequest => pullRequest.IsStale).IsRequired();
        builder.Property(pullRequest => pullRequest.CreatedAt).IsRequired();
        builder.Property(pullRequest => pullRequest.UpdatedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(pullRequest => pullRequest.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(pullRequest => new { pullRequest.SessionId, pullRequest.Number })
            .IsUnique()
            .HasDatabaseName("ux_pull_requests_session_id_number");

        // The deployment webhook of section 18 arrives keyed on the head commit, not on our own ids.
        builder.HasIndex(pullRequest => pullRequest.HeadSha).HasDatabaseName("ix_pull_requests_head_sha");
    }
}

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");
        builder.HasKey(deployment => deployment.Id);

        builder.Property(deployment => deployment.Id).ValueGeneratedNever();
        builder.Property(deployment => deployment.Provider).HasMaxLength(60).IsRequired();
        builder.Property(deployment => deployment.Url).HasMaxLength(1000);
        builder.Property(deployment => deployment.State).HasEnumConversion();
        builder.Property(deployment => deployment.ReportedAt).IsRequired();

        builder.HasOne<PullRequest>()
            .WithMany()
            .HasForeignKey(deployment => deployment.PullRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(deployment => new { deployment.PullRequestId, deployment.ReportedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_deployments_pull_request_id_reported_at");
    }
}

internal sealed class VerificationArtifactConfiguration : IEntityTypeConfiguration<VerificationArtifact>
{
    public void Configure(EntityTypeBuilder<VerificationArtifact> builder)
    {
        builder.ToTable("verification_artifacts");
        builder.HasKey(artifact => artifact.Id);

        builder.Property(artifact => artifact.Id).ValueGeneratedNever();
        builder.Property(artifact => artifact.Kind).HasEnumConversion();
        builder.Property(artifact => artifact.State).HasEnumConversion();
        builder.Property(artifact => artifact.Url).HasMaxLength(1000);

        // An object storage key. Section 27.5: an IPA is not a database row, and section 2.3 rules
        // out the container filesystem entirely.
        builder.Property(artifact => artifact.FileRef).HasMaxLength(500);
        builder.Property(artifact => artifact.ConnectString).HasMaxLength(500);
        builder.Property(artifact => artifact.Audience).HasEnumConversion();
        builder.Property(artifact => artifact.CreatedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(artifact => artifact.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(artifact => artifact.SessionId).HasDatabaseName("ix_verification_artifacts_session_id");

        // The pruning job of section 27.5, without which storage costs run away.
        builder.HasIndex(artifact => artifact.ExpiresAt)
            .HasFilter("expires_at IS NOT NULL")
            .HasDatabaseName("ix_verification_artifacts_expires_at");
    }
}
