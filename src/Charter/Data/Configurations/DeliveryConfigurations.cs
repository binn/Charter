using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class ChangeRequestConfiguration : IEntityTypeConfiguration<ChangeRequest>
{
    public void Configure(EntityTypeBuilder<ChangeRequest> builder)
    {
        builder.ToTable("change_requests");
        builder.HasKey(changeRequest => changeRequest.Id);

        builder.Property(changeRequest => changeRequest.Id).ValueGeneratedNever();
        builder.Property(changeRequest => changeRequest.Number).IsRequired();
        builder.Property(changeRequest => changeRequest.Url).HasMaxLength(500).IsRequired();
        builder.Property(changeRequest => changeRequest.HeadSha).HasMaxLength(64).IsRequired();

        // Section 27.7 names the branch in the engineer `Details` disclosure. 255 is git's own
        // practical ref limit; optional because a webhook can arrive without a ref — and because a
        // provider with no branches (change spec 001 part A.7) has none to report.
        builder.Property(changeRequest => changeRequest.HeadBranch).HasMaxLength(255);

        // Section 18: the name a preview provider needs when it has to tell an operator who to invite
        // to the workspace. 255 covers any provider's login; optional because not every provider
        // reports one, and "the author" is what the warning falls back to when nothing was recorded.
        builder.Property(changeRequest => changeRequest.AuthorLogin).HasMaxLength(255);

        builder.Property(changeRequest => changeRequest.State).HasEnumConversion();
        builder.Property(changeRequest => changeRequest.IsStale).IsRequired();
        builder.Property(changeRequest => changeRequest.CreatedAt).IsRequired();
        builder.Property(changeRequest => changeRequest.UpdatedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(changeRequest => changeRequest.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(changeRequest => new { changeRequest.SessionId, changeRequest.Number })
            .IsUnique()
            .HasDatabaseName("ux_change_requests_session_id_number");

        // The deployment webhook of section 18 arrives keyed on the head commit, not on our own ids.
        builder.HasIndex(changeRequest => changeRequest.HeadSha).HasDatabaseName("ix_change_requests_head_sha");
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

        builder.HasOne<ChangeRequest>()
            .WithMany()
            .HasForeignKey(deployment => deployment.ChangeRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(deployment => new { deployment.ChangeRequestId, deployment.ReportedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_deployments_change_request_id_reported_at");
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

        // Section 27.7's kind-specific body: checksums, sizes, capture lists, test counts, device
        // identifiers. jsonb because the eight kinds share almost no fields and the domain owns no
        // serialiser - the same call as Event.Payload.
        builder.Property(artifact => artifact.Payload).IsOptionalJsonb();

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
