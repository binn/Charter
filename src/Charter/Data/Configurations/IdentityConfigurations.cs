using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");
        builder.HasKey(organization => organization.Id);

        builder.Property(organization => organization.Id).ValueGeneratedNever();
        builder.Property(organization => organization.Name).HasMaxLength(200).IsRequired();
        builder.Property(organization => organization.Mode).HasEnumConversion();
        builder.Property(organization => organization.CreatedAt).IsRequired();
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).ValueGeneratedNever();

        // 320 is the maximum length of an addr-spec: 64 local part, @, 255 domain.
        builder.Property(user => user.Email).HasMaxLength(320).IsRequired();
        builder.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.TeachingLevel).HasEnumConversion();

        // Section 12. There is no browser storage in this app (section 3.1), so these columns are the
        // only place a preference exists; a PATCH that had nowhere to land would be a silently
        // dropped write.
        builder.Property(user => user.Theme).HasEnumConversion();
        builder.Property(user => user.Pane).HasEnumConversion();

        // Section 30.4. Null until the three screens are done.
        builder.Property(user => user.RequesterOnboardingCompletedAt);

        builder.Property(user => user.CreatedAt).IsRequired();

        builder.HasIndex(user => user.Email).IsUnique().HasDatabaseName("ux_users_email");
    }
}

internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("members");
        builder.HasKey(member => member.Id);

        builder.Property(member => member.Id).ValueGeneratedNever();
        builder.Property(member => member.CreatedAt).IsRequired();

        // Section 7.1: roles are additive and always read with the member, so they are a Postgres
        // text[] rather than a join table. `roles @> '{approver}'` stays indexable, and the
        // permission check stays a single row read.
        builder.PrimitiveCollection(member => member.Roles)
            .HasEnumElements<IReadOnlyList<MemberRole>, MemberRole>()
            .IsRequired();

        builder.PrimitiveCollection(member => member.Capabilities)
            .HasEnumElements<IReadOnlyList<MemberCapability>, MemberCapability>()
            .IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(member => member.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(member => new { member.OrgId, member.UserId })
            .IsUnique()
            .HasDatabaseName("ux_members_org_id_user_id");

        builder.HasIndex(member => member.UserId).HasDatabaseName("ix_members_user_id");

        builder.HasIndex(member => member.Roles)
            .HasMethod("gin")
            .HasDatabaseName("ix_members_roles");
    }
}

internal sealed class IdentityConfiguration : IEntityTypeConfiguration<Domain.Identity>
{
    public void Configure(EntityTypeBuilder<Domain.Identity> builder)
    {
        builder.ToTable("identities");
        builder.HasKey(identity => identity.Id);

        builder.Property(identity => identity.Id).ValueGeneratedNever();
        builder.Property(identity => identity.Provider).HasEnumConversion();
        builder.Property(identity => identity.ProviderUserId).HasMaxLength(200).IsRequired();

        // Section 21: the password provider's verifier. Sized for an ASP.NET Core PasswordHasher V3
        // string (84 base64 characters today) with room for a future format, and deliberately not
        // indexed - nothing ever looks a user up by their hash.
        builder.Property(identity => identity.SecretHash).HasMaxLength(400);

        builder.Property(identity => identity.CreatedAt).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One Charter user per provider subject: this is what makes an inbound Slack or Discord
        // message resolve to a requester (section 21).
        builder.HasIndex(identity => new { identity.Provider, identity.ProviderUserId })
            .IsUnique()
            .HasDatabaseName("ux_identities_provider_provider_user_id");

        builder.HasIndex(identity => identity.UserId).HasDatabaseName("ix_identities_user_id");
    }
}
