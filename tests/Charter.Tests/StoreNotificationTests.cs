using Charter.Data.Notifications;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 22's preference table and change spec 001 C.3's delivery log, against a real Postgres.
/// </summary>
public class StoreNotificationTests
{
    [Fact]
    public async Task SomebodyWithNoStoredPreferenceGetsEmailAndNothingElse()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var store = new EfNotificationPreferenceStore(fixture.Scopes, fixture.Clock);

        // The default has to survive with no backfill: every user who existed before the table did
        // has no row, and must read back exactly what DefaultNotificationPreferenceStore gave them.
        var preference = await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Equal(NotificationPreference.Default(fixture.UserId).Channels, preference.Channels);
        Assert.Contains(NotificationChannel.Email, preference.Channels);
        Assert.DoesNotContain(NotificationChannel.Slack, preference.Channels);
        Assert.DoesNotContain(NotificationChannel.Discord, preference.Channels);
        Assert.True(preference.WantsAnything);

        var rows = await fixture.WithContextAsync(db => db.NotificationChannels
            .CountAsync(channel => channel.UserId == fixture.UserId, TestContext.Current.CancellationToken));

        // Reading a preference must not create one. A default that writes itself down is a default
        // that can never be changed afterwards.
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task TurningEveryChannelOffIsAnEmptySetRatherThanAPerStateColumn()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var store = new EfNotificationPreferenceStore(fixture.Scopes, fixture.Clock);

        await store.SetAsync(fixture.UserId, NotificationChannel.Email, false, TestContext.Current.CancellationToken);

        var preference = await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        // Section 22: two states fire, and somebody who wants neither wants no notifications.
        Assert.Empty(preference.Channels);
        Assert.False(preference.WantsAnything);

        // Switching a declared-but-unimplemented channel on is a row, not a migration.
        await store.SetAsync(fixture.UserId, NotificationChannel.Slack, true, TestContext.Current.CancellationToken);

        var updated = await store.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal([NotificationChannel.Slack], updated.Channels);
    }

    [Fact]
    public async Task APreferenceSurvivesTheProcessAndIsOneRowPerChannel()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var store = new EfNotificationPreferenceStore(fixture.Scopes, fixture.Clock);

        await store.SetAsync(fixture.UserId, NotificationChannel.Discord, true, TestContext.Current.CancellationToken);
        await store.SetAsync(fixture.UserId, NotificationChannel.Discord, false, TestContext.Current.CancellationToken);
        await store.SetAsync(fixture.UserId, NotificationChannel.Discord, true, TestContext.Current.CancellationToken);

        var rows = await fixture.WithContextAsync(db => db.NotificationChannels
            .Where(channel => channel.UserId == fixture.UserId)
            .ToListAsync(TestContext.Current.CancellationToken));

        // The key is (user, channel), so toggling is an update and not an audit trail.
        var row = Assert.Single(rows);
        Assert.Equal(NotificationChannelKind.Discord, row.Channel);
        Assert.True(row.Enabled);

        var reopened = new EfNotificationPreferenceStore(fixture.Scopes, fixture.Clock);
        var preference = await reopened.GetAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Contains(NotificationChannel.Discord, preference.Channels);
        Assert.Contains(NotificationChannel.Email, preference.Channels);
    }

    [Fact]
    public async Task ADeliveryFailureIsStillThereAfterTheContainerRestarts()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.ClearDeliveriesAsync();

        var log = new EfEmailDeliveryLog(fixture.Scopes, fixture.Clock, NullLogger<EfEmailDeliveryLog>.Instance);

        log.Record(new EmailDeliveryRecord
        {
            At = fixture.Clock.GetUtcNow(),
            Recipient = "Ada@Example.com",
            Kind = "invitation",
            Status = EmailDeliveryStatus.Sent,
            Summary = "Sent to Ada.",
        });

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);

        log.Record(new EmailDeliveryRecord
        {
            At = fixture.Clock.GetUtcNow(),
            Recipient = "grace@example.com",
            Kind = "needs_input",
            Status = EmailDeliveryStatus.Failed,
            Summary = "The mail server refused it.",
            Detail = "550 5.7.1 Relay access denied",
        });

        // A new instance, as a redeploy would have. This is the whole point: a redeploy is the usual
        // response to "mail seems broken", and it used to be what emptied the list.
        var reopened = new EfEmailDeliveryLog(
            fixture.Scopes,
            fixture.Clock,
            NullLogger<EfEmailDeliveryLog>.Instance);

        var recent = reopened.Recent(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("grace@example.com", recent[0].Recipient);
        Assert.Equal("ada@example.com", recent[1].Recipient);

        var failure = reopened.LastFailure;
        Assert.NotNull(failure);
        Assert.Equal(EmailDeliveryStatus.Failed, failure.Status);
        Assert.Equal("550 5.7.1 Relay access denied", failure.Detail);
    }

    [Fact]
    public async Task RecordingADeliveryNeverBreaksTheDeliveryItIsRecording()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.ClearDeliveriesAsync();

        var log = new EfEmailDeliveryLog(fixture.Scopes, fixture.Clock, NullLogger<EfEmailDeliveryLog>.Instance);

        // Longer than every column allows. An invitation that 500s because the diagnostic list
        // refused a long SMTP reply is a person who cannot join.
        log.Record(new EmailDeliveryRecord
        {
            At = fixture.Clock.GetUtcNow(),
            Recipient = "someone@example.com",
            Kind = new string('k', 200),
            Status = EmailDeliveryStatus.RateLimited,
            Summary = new string('s', 900),
            Detail = new string('d', 4000),
        });

        var stored = Assert.Single(log.Recent(5), record => record.Recipient == "someone@example.com");

        Assert.Equal(EmailDelivery.MaxKindLength, stored.Kind.Length);
        Assert.Equal(EmailDelivery.MaxSummaryLength, stored.Summary.Length);
        Assert.Equal(EmailDelivery.MaxDetailLength, stored.Detail?.Length);
    }

    [Fact]
    public async Task RetentionActuallyPrunesTheDeliveryLog()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.ClearDeliveriesAsync();

        var log = new EfEmailDeliveryLog(fixture.Scopes, fixture.Clock, NullLogger<EfEmailDeliveryLog>.Instance);
        var start = fixture.Clock.GetUtcNow();

        for (var index = 0; index < 5; index++)
        {
            log.Record(new EmailDeliveryRecord
            {
                At = start.AddMinutes(index),
                Recipient = $"old-{index}@example.com",
                Kind = "needs_input",
                Status = EmailDeliveryStatus.Sent,
                Summary = "Sent.",
            });
        }

        // Two months later, one more send. Everything above is past retention.
        fixture.Clock.Now = start.Add(EfEmailDeliveryLog.Retention).AddDays(30);

        log.Record(new EmailDeliveryRecord
        {
            At = fixture.Clock.GetUtcNow(),
            Recipient = "current@example.com",
            Kind = "invitation",
            Status = EmailDeliveryStatus.Sent,
            Summary = "Sent.",
        });

        // Retention is Charter's job, not an operator's: that last write swept everything past the
        // window on its way through. An unbounded record of every email an instance ever sent grows
        // without limit and is a standing list of who was contacted; the recent ones are what the
        // settings page is for, and they are still here.
        var remaining = await fixture.WithContextAsync(db => db.EmailDeliveries
            .Select(delivery => delivery.Recipient)
            .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["current@example.com"], remaining);
        Assert.Contains(log.Recent(50), record => record.Recipient == "current@example.com");

        // And it is idempotent: a second sweep with nothing to sweep removes nothing.
        Assert.Equal(0, log.Prune());
    }
}
