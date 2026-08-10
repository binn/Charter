using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class NotificationChannelPreferenceConfiguration
    : IEntityTypeConfiguration<NotificationChannelPreference>
{
    public void Configure(EntityTypeBuilder<NotificationChannelPreference> builder)
    {
        builder.ToTable("notification_channels");

        // Section 22: the key is (user, channel) and nothing else. There is no per-state column and
        // there must not be one - two states fire, and wanting neither is an empty channel set.
        builder.HasKey(preference => new { preference.UserId, preference.Channel });

        builder.Property(preference => preference.Channel).HasEnumConversion();
        builder.Property(preference => preference.Enabled).IsRequired();
        builder.Property(preference => preference.UpdatedAt).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(preference => preference.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EmailDeliveryConfiguration : IEntityTypeConfiguration<EmailDelivery>
{
    public void Configure(EntityTypeBuilder<EmailDelivery> builder)
    {
        builder.ToTable("email_deliveries");
        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Id).ValueGeneratedNever();
        builder.Property(delivery => delivery.At).IsRequired();
        builder.Property(delivery => delivery.Recipient)
            .HasMaxLength(EmailDelivery.MaxRecipientLength)
            .IsRequired();
        builder.Property(delivery => delivery.Kind).HasMaxLength(EmailDelivery.MaxKindLength).IsRequired();
        builder.Property(delivery => delivery.Outcome).HasEnumConversion();
        builder.Property(delivery => delivery.Summary).HasMaxLength(EmailDelivery.MaxSummaryLength).IsRequired();
        builder.Property(delivery => delivery.Detail).HasMaxLength(EmailDelivery.MaxDetailLength);

        // Deliberately no foreign key to `users`: mail goes to invitees who have no account yet, and
        // a delivery log that could only record members would be empty exactly when an operator is
        // debugging why an invitation never arrived.

        // Two indexes on one column, so both are declared by name: EF identifies an index by the
        // properties it covers, and a second HasIndex(delivery => delivery.At) would reconfigure the
        // first rather than add another.

        // The settings page reads newest first, and retention sweeps the same column from the far end.
        builder.HasIndex([nameof(EmailDelivery.At)], "ix_email_deliveries_at")
            .IsDescending(true)
            .HasDatabaseName("ix_email_deliveries_at");

        // "What was the last failure" leads the settings page, so it gets its own partial index
        // rather than scanning every successful send to find one.
        builder.HasIndex([nameof(EmailDelivery.At)], "ix_email_deliveries_failed_at")
            .IsDescending(true)
            .HasFilter("outcome = 'failed'")
            .HasDatabaseName("ix_email_deliveries_failed_at");
    }
}
