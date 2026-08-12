using Charter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Charter.Notifications;

/// <summary>Registers email delivery and the section 22 notification fan-out.</summary>
public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the email provider, the send path, and the notification channels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <see cref="ConfigurationServiceCollectionExtensions.AddCharterConfig"/> to have run:
    /// the provider is chosen from the validated <see cref="EmailConfig"/> rather than by reading
    /// the environment again.
    /// </para>
    /// <para>
    /// Which <see cref="IEmailProvider"/> is registered is decided here, once, at startup - not per
    /// call by asking whether email is configured. A caller injecting <see cref="IEmailSender"/>
    /// gets the same object either way and the same result shape either way, which is what makes
    /// <c>none</c> a configuration rather than a branch in every feature that sends mail.
    /// </para>
    /// <para>
    /// Everything is <c>TryAdd</c>, so a host or a test that registers its own transport, preference
    /// store or delivery log before calling this keeps it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);

        services.TryAddSingleton<IEmailDeliveryLog, RecentEmailDeliveryLog>();

        services.TryAddSingleton<IEmailRateLimiter>(provider => new EmailRateLimiter(
            provider.GetRequiredService<EmailConfig>().MaxPerRecipientPerHour,
            provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<ISmtpTransport>(provider =>
        {
            var config = provider.GetRequiredService<EmailConfig>();

            // Unreachable unless email is enabled, because nothing resolves this otherwise. The
            // throw is here so a future caller that resolves it under `none` finds out immediately
            // rather than sending into a transport with no endpoint.
            var smtp = config.Smtp
                       ?? throw new InvalidOperationException(
                           "No SMTP endpoint is configured. CHARTER_EMAIL_PROVIDER is " +
                           $"'{config.ProviderToken}'.");

            return new SmtpTransport(smtp, provider.GetRequiredService<ILogger<SmtpTransport>>());
        });

        services.TryAddSingleton<IEmailProvider>(provider =>
        {
            var config = provider.GetRequiredService<EmailConfig>();

            return config.Enabled
                ? new SmtpEmailProvider(
                    config,
                    provider.GetRequiredService<ISmtpTransport>(),
                    provider.GetRequiredService<TimeProvider>())
                : new NullEmailProvider();
        });

        services.TryAddSingleton<IEmailSender>(provider => new EmailSender(
            provider.GetRequiredService<IEmailProvider>(),
            provider.GetRequiredService<IEmailRateLimiter>(),
            provider.GetRequiredService<IEmailDeliveryLog>(),
            provider.GetRequiredService<EmailConfig>(),
            provider.GetRequiredService<ILogger<EmailSender>>(),
            provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IAccountMailer, AccountMailer>();

        services.TryAddSingleton<IEmailTester>(provider => new EmailTester(
            provider.GetRequiredService<IEmailSender>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<CharterConfig>().BaseUrl.Host));

        // Section 22: one outbound abstraction, per-user channel preference. The preference store is
        // TryAdd so the persistence layer can supply a column-backed one without touching this file.
        services.TryAddSingleton<INotificationPreferenceStore, DefaultNotificationPreferenceStore>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<INotificationChannel, EmailNotificationChannel>());

        services.TryAddSingleton<INotificationService, NotificationService>();

        return services;
    }
}
