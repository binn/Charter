using Charter.Configuration;
using Charter.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// The registrations <c>Program.cs</c> gets from one <c>AddCharterNotifications()</c> call.
/// </summary>
/// <remarks>
/// Which provider is registered is decided once, at startup, from the validated configuration -
/// not per call by asking whether email happens to be configured. These tests pin that, because it
/// is what makes <c>none</c> a configuration rather than a branch inside every feature that sends
/// mail.
/// </remarks>
public class NotificationWiringTests
{
    [Fact]
    public void RegistersTheSmtpProviderWhenEmailIsConfigured()
    {
        using var provider = Build(
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        var email = provider.GetRequiredService<IEmailProvider>();

        Assert.IsType<SmtpEmailProvider>(email);
        Assert.Equal("smtp", email.Name);
        Assert.True(email.IsEnabled);
        Assert.True(provider.GetRequiredService<IEmailSender>().Availability.Enabled);
    }

    [Fact]
    public void RegistersTheDisabledProviderWhenEmailIsOff()
    {
        using var provider = Build();

        var email = provider.GetRequiredService<IEmailProvider>();

        Assert.IsType<NullEmailProvider>(email);
        Assert.False(email.IsEnabled);

        var availability = provider.GetRequiredService<IEmailSender>().Availability;
        Assert.False(availability.Enabled);
        Assert.Equal("none", availability.Provider);
        Assert.NotNull(availability.HowToEnable);
    }

    [Fact]
    public void EverythingASettingsPageNeedsResolves()
    {
        using var provider = Build(
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        Assert.NotNull(provider.GetRequiredService<IEmailTester>());
        Assert.NotNull(provider.GetRequiredService<IEmailDeliveryLog>());
        Assert.NotNull(provider.GetRequiredService<IAccountMailer>());
        Assert.NotNull(provider.GetRequiredService<INotificationService>());
        Assert.NotNull(provider.GetRequiredService<ISmtpTransport>());
    }

    [Fact]
    public void TheRateLimitComesFromConfiguration()
    {
        using var provider = Build(("CHARTER_EMAIL_MAX_PER_HOUR", "3"));

        var limiter = Assert.IsType<EmailRateLimiter>(provider.GetRequiredService<IEmailRateLimiter>());

        Assert.Equal(3, limiter.Limit);
    }

    [Fact]
    public void EmailIsTheOnlyChannelWithAnImplementation()
    {
        // Section 22 names Email, Slack and Discord. Change spec 001 says one implementation per
        // seam until the loop works, and this is which one.
        using var provider = Build();

        var channel = Assert.Single(provider.GetServices<INotificationChannel>());

        Assert.Equal(NotificationChannel.Email, channel.Channel);
    }

    [Fact]
    public void AHostCanSubstituteItsOwnTransportOrPreferenceStore()
    {
        // Everything is TryAdd, so the persistence layer can supply a column-backed preference store
        // without this file changing.
        var services = Services(
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        services.AddSingleton<ISmtpTransport>(new StubSmtpTransport());
        services.AddSingleton<INotificationPreferenceStore>(new FixedNotificationPreferenceStore());
        services.AddCharterNotifications();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<StubSmtpTransport>(provider.GetRequiredService<ISmtpTransport>());
        Assert.IsType<FixedNotificationPreferenceStore>(
            provider.GetRequiredService<INotificationPreferenceStore>());
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] overrides)
    {
        var services = Services(overrides);
        services.AddCharterNotifications();

        return services.BuildServiceProvider();
    }

    private static ServiceCollection Services(params (string Key, string? Value)[] overrides)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterConfig(ConfigTestEnvironment.Valid(overrides));

        return services;
    }
}
