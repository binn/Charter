using Charter.Notifications;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// Covers change spec 001 part C.3: outbound mail is rate-limited per recipient so a notification
/// storm cannot happen.
/// </summary>
public class EmailRateLimitTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheLimitIsPerRecipient()
    {
        // The failure this prevents is one person being buried, not the instance sending a lot of
        // mail. Two recipients each get their own allowance.
        var clock = new ModelFakeTimeProvider(Start);
        var limiter = new EmailRateLimiter(2, clock);

        Assert.True(limiter.TryAcquire("one@example.com", EmailCategory.Notification, out _));
        Assert.True(limiter.TryAcquire("one@example.com", EmailCategory.Notification, out _));
        Assert.False(limiter.TryAcquire("one@example.com", EmailCategory.Notification, out _));

        Assert.True(limiter.TryAcquire("two@example.com", EmailCategory.Notification, out _));
    }

    [Fact]
    public void ACapitalisedAddressIsTheSameRecipient()
    {
        var limiter = new EmailRateLimiter(1, new ModelFakeTimeProvider(Start));

        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        Assert.False(limiter.TryAcquire("Person@Example.com", EmailCategory.Notification, out _));
    }

    [Fact]
    public void AStormOfNotificationsCannotStarveAnInvitation()
    {
        // Two buckets, because they fail in opposite directions: a late notification is a nuisance,
        // a late invitation is a new hire who cannot log in.
        var limiter = new EmailRateLimiter(1, new ModelFakeTimeProvider(Start));

        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        Assert.False(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Transactional, out _));
    }

    [Fact]
    public void TheWindowSlidesRatherThanResetting()
    {
        var clock = new ModelFakeTimeProvider(Start);
        var limiter = new EmailRateLimiter(2, clock);

        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));

        Assert.False(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(30), retryAfter);

        // Only the first send ages out, so exactly one slot opens.
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        Assert.False(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
    }

    [Fact]
    public void RemainingReportsWhatIsLeft()
    {
        var limiter = new EmailRateLimiter(3, new ModelFakeTimeProvider(Start));

        Assert.Equal(3, limiter.Remaining("person@example.com", EmailCategory.Notification));
        Assert.True(limiter.TryAcquire("person@example.com", EmailCategory.Notification, out _));
        Assert.Equal(2, limiter.Remaining("person@example.com", EmailCategory.Notification));
    }

    [Fact]
    public async Task HeldBackMailIsRecordedAndWarnedAboutRatherThanDroppedQuietly()
    {
        // Held back is not the same as failed, and neither is the same as sent. The settings page
        // has to be able to tell an administrator which of the three happened.
        var clock = new ModelFakeTimeProvider(Start);
        var config = EmailFixture.Enabled(maxPerHour: 1);
        var provider = new StubEmailProvider();

        var logger = new RecordingLogger<EmailSender>();
        var log = new RecentEmailDeliveryLog();
        var sender = new EmailSender(
            provider,
            new EmailRateLimiter(config.MaxPerRecipientPerHour, clock),
            log,
            config,
            logger,
            clock);

        var first = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);
        var second = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.True(first.Delivered);
        Assert.Equal(EmailDeliveryStatus.RateLimited, second.Status);
        Assert.NotNull(second.RetryAfter);
        Assert.Single(provider.Sent);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            [EmailDeliveryStatus.RateLimited, EmailDeliveryStatus.Sent],
            log.Recent().Select(record => record.Status));
    }
}
