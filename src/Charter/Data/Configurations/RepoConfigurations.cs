using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class RepoConfiguration : IEntityTypeConfiguration<Repo>
{
    public void Configure(EntityTypeBuilder<Repo> builder)
    {
        builder.ToTable("repos");
        builder.HasKey(repo => repo.Id);

        builder.Property(repo => repo.Id).ValueGeneratedNever();
        builder.Property(repo => repo.GithubInstallationId).IsRequired();
        builder.Property(repo => repo.FullName).HasMaxLength(255).IsRequired();
        builder.Property(repo => repo.BaseBranch).HasMaxLength(255).IsRequired();
        builder.Property(repo => repo.Status).HasEnumConversion();
        builder.Property(repo => repo.CharterConfigSnapshot).IsOptionalJsonb();
        builder.Property(repo => repo.PrimerMd);
        builder.Property(repo => repo.CreatedAt).IsRequired();
        builder.Property(repo => repo.UpdatedAt).IsRequired();

        // Derived from Status. Storing it would let the two disagree.
        builder.Ignore(repo => repo.IsRequesterVisible);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(repo => repo.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(repo => new { repo.OrgId, repo.FullName })
            .IsUnique()
            .HasDatabaseName("ux_repos_org_id_full_name");

        builder.HasIndex(repo => repo.GithubInstallationId).HasDatabaseName("ix_repos_github_installation_id");
    }
}

internal sealed class RepoScopeConfiguration : IEntityTypeConfiguration<RepoScope>
{
    public void Configure(EntityTypeBuilder<RepoScope> builder)
    {
        // Section 7.3: deny by default. The absence of a row is a refusal, which is why there is no
        // seeded "everyone" grant anywhere in this schema.
        builder.ToTable("repo_scopes", table => table.HasCheckConstraint(
            "ck_repo_scopes_member_xor_role",
            "(member_id IS NULL) <> (role IS NULL)"));

        builder.HasKey(scope => scope.Id);

        builder.Property(scope => scope.Id).ValueGeneratedNever();
        builder.Property(scope => scope.Role).HasEnumConversion();
        builder.Property(scope => scope.CanRequest).IsRequired();
        builder.Property(scope => scope.CreatedAt).IsRequired();

        builder.HasOne<Repo>()
            .WithMany()
            .HasForeignKey(scope => scope.RepoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(scope => scope.MemberId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(scope => new { scope.RepoId, scope.MemberId })
            .IsUnique()
            .HasFilter("member_id IS NOT NULL")
            .HasDatabaseName("ux_repo_scopes_repo_id_member_id");

        builder.HasIndex(scope => new { scope.RepoId, scope.Role })
            .IsUnique()
            .HasFilter("role IS NOT NULL")
            .HasDatabaseName("ux_repo_scopes_repo_id_role");
    }
}

internal sealed class AutoDispatchPolicyConfiguration : IEntityTypeConfiguration<AutoDispatchPolicy>
{
    public void Configure(EntityTypeBuilder<AutoDispatchPolicy> builder)
    {
        builder.ToTable("auto_dispatch_policies");
        builder.HasKey(policy => policy.Id);

        builder.Property(policy => policy.Id).ValueGeneratedNever();
        builder.Property(policy => policy.Role).HasEnumConversion();
        builder.Property(policy => policy.Enabled).IsRequired();
        builder.Property(policy => policy.MaxCostUsd).IsMoney();
        builder.Property(policy => policy.RequireApprovalAboveUsd).IsMoney();
        builder.Property(policy => policy.CreatedAt).IsRequired();
        builder.Property(policy => policy.UpdatedAt).IsRequired();

        builder.PrimitiveCollection(policy => policy.AllowedPaths).IsRequired();
        builder.PrimitiveCollection(policy => policy.ProjectTypes)
            .HasEnumElements<IReadOnlyList<ProjectType>, ProjectType>()
            .IsRequired();

        // A pure function of the scope columns; see AutoDispatchPolicy.Specificity.
        builder.Ignore(policy => policy.Specificity);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(policy => policy.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Repo>()
            .WithMany()
            .HasForeignKey(policy => policy.RepoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(policy => policy.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Resolution reads every policy for the org and picks the most specific match, so the index
        // that matters is the one that fetches the candidate set.
        builder.HasIndex(policy => new { policy.OrgId, policy.Enabled })
            .HasDatabaseName("ix_auto_dispatch_policies_org_id_enabled");

        builder.HasIndex(policy => policy.RepoId).HasDatabaseName("ix_auto_dispatch_policies_repo_id");
        builder.HasIndex(policy => policy.UserId).HasDatabaseName("ix_auto_dispatch_policies_user_id");
    }
}
