using Charter.Domain;
using Charter.Notifications;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>An <see cref="INotificationChannel"/> that records what it was asked to deliver.</summary>
internal sealed class RecordingNotificationChannel : INotificationChannel
{
    public RecordingNotificationChannel(NotificationChannel channel, bool available = true)
    {
        Channel = channel;
        IsAvailable = available;
    }

    public NotificationChannel Channel { get; }

    public bool IsAvailable { get; set; }

    public EmailDeliveryResult Next { get; set; } = EmailDeliveryResult.Sent();

    public List<RequestNotification> Sent { get; } = [];

    public Task<EmailDeliveryResult> SendAsync(
        RequestNotification notification,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(notification);
        return Task.FromResult(Next);
    }
}

/// <summary>A preference store the test sets by hand.</summary>
internal sealed class FixedNotificationPreferenceStore : INotificationPreferenceStore
{
    private readonly NotificationChannel[] channels;

    public FixedNotificationPreferenceStore(params NotificationChannel[] channels) => this.channels = channels;

    public Task<NotificationPreference> GetAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationPreference
        {
            UserId = userId,
            Channels = new HashSet<NotificationChannel>(channels),
        });
}

/// <summary>
/// Section 22: one outbound abstraction with a per-user channel preference, firing on the two
/// notify-worthy states of section 6 and on nothing else.
/// </summary>
public class NotificationDispatchTests
{
    [Fact]
    public void ExactlyTwoStatesNotify()
    {
        // Section 6 is blunt about this: notifying on all of them gets Charter muted within a week.
        // Pinning the set here is what makes adding a third an edit to a test rather than a call
        // site somebody adds on a Friday.
        Assert.Equal(
            [RequestStatus.NeedsInput, RequestStatus.PreviewReady],
            NotifyWorthyStates.All.OrderBy(status => status));

        Assert.Equal(2, NotifyWorthyStates.All.Count);
    }

    [Theory]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Refining)]
    [InlineData(RequestStatus.SpecReady)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Queued)]
    [InlineData(RequestStatus.Running)]
    [InlineData(RequestStatus.PrOpen)]
    [InlineData(RequestStatus.InReview)]
    [InlineData(RequestStatus.Merged)]
    [InlineData(RequestStatus.Failed)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Stale)]
    public async Task EveryOtherStateIsSilent(RequestStatus status)
    {
        // Failed is the instructive one. Section 6 renders it as "an engineer has been notified" -
        // somebody is told, it is not the requester, and it is not by this path.
        Assert.False(NotifyWorthyStates.Notifies(status));

        var channel = new RecordingNotificationChannel(NotificationChannel.Email);
        var service = Service(channel);

        var outcome = await service.NotifyAsync(
            Notification(status),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationOutcomeKind.NotNotifyWorthy, outcome.Kind);
        Assert.Empty(channel.Sent);
    }

    [Theory]
    [InlineData(RequestStatus.NeedsInput)]
    [InlineData(RequestStatus.PreviewReady)]
    public async Task TheTwoNotifyWorthyStatesReachTheRecipient(RequestStatus status)
    {
        var channel = new RecordingNotificationChannel(NotificationChannel.Email);
        var service = Service(channel);

        var outcome = await service.NotifyAsync(
            Notification(status),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationOutcomeKind.Delivered, outcome.Kind);
        Assert.Equal(status, Assert.Single(channel.Sent).Status);
        Assert.Equal(NotificationChannel.Email, Assert.Single(outcome.Channels).Channel);
    }

    [Fact]
    public async Task OnlyTheChannelsAPersonAskedForAreUsed()
    {
        var email = new RecordingNotificationChannel(NotificationChannel.Email);
        var slack = new RecordingNotificationChannel(NotificationChannel.Slack);

        var service = new NotificationService(
            [email, slack],
            new FixedNotificationPreferenceStore(NotificationChannel.Slack),
            new RecordingLogger<NotificationService>());

        _ = await service.NotifyAsync(
            Notification(RequestStatus.NeedsInput),
            TestContext.Current.CancellationToken);

        Assert.Empty(email.Sent);
        Assert.Single(slack.Sent);
    }

    [Fact]
    public async Task SomebodyWhoHasTurnedEverythingOffIsNotEmailedAnyway()
    {
        var email = new RecordingNotificationChannel(NotificationChannel.Email);

        var service = new NotificationService(
            [email],
            new FixedNotificationPreferenceStore(),
            new RecordingLogger<NotificationService>());

        var outcome = await service.NotifyAsync(
            Notification(RequestStatus.PreviewReady),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationOutcomeKind.OptedOut, outcome.Kind);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task AChannelThatIsNotConfiguredOnThisInstanceIsSkipped()
    {
        var email = new RecordingNotificationChannel(NotificationChannel.Email, available: false);
        var service = Service(email);

        var outcome = await service.NotifyAsync(
            Notification(RequestStatus.NeedsInput),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationOutcomeKind.NoChannelAvailable, outcome.Kind);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task AFailedNotificationIsReportedAndLoggedRatherThanSwallowed()
    {
        var email = new RecordingNotificationChannel(NotificationChannel.Email)
        {
            Next = EmailDeliveryResult.Failed("The mail server refused this message.", "550 relay denied"),
        };

        var logger = new RecordingLogger<NotificationService>();
        var service = new NotificationService(
            [email],
            new FixedNotificationPreferenceStore(NotificationChannel.Email),
            logger);

        var outcome = await service.NotifyAsync(
            Notification(RequestStatus.PreviewReady),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationOutcomeKind.Failed, outcome.Kind);
        Assert.False(outcome.Delivered);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TheEmailChannelPicksTheTemplateThatMatchesTheState()
    {
        var config = EmailFixture.Enabled();
        var provider = new StubEmailProvider();
        var sender = EmailFixture.Sender(provider, config, out _, out _);
        var channel = new EmailNotificationChannel(sender);

        _ = await channel.SendAsync(
            Notification(RequestStatus.NeedsInput),
            TestContext.Current.CancellationToken);

        _ = await channel.SendAsync(
            Notification(RequestStatus.PreviewReady),
            TestContext.Current.CancellationToken);

        Assert.Equal(["needs_input", "preview_ready"], provider.Sent.Select(message => message.Kind));
        Assert.Equal("A question about your request", provider.Sent[0].Content.Subject);
        Assert.Equal("Ready to try", provider.Sent[1].Content.Subject);

        // Notifications are rate-limited in their own bucket, so a storm cannot starve an invitation.
        Assert.All(provider.Sent, message => Assert.Equal(EmailCategory.Notification, message.Category));
    }

    [Fact]
    public async Task AnUnusableAddressIsAFailureWithAReasonRatherThanAnException()
    {
        var sender = EmailFixture.Sender(new StubEmailProvider(), EmailFixture.Enabled(), out _, out _);
        var channel = new EmailNotificationChannel(sender);

        var result = await channel.SendAsync(
            Notification(RequestStatus.NeedsInput) with
            {
                Recipient = new NotificationRecipient
                {
                    UserId = Guid.NewGuid(),
                    Email = "not-an-address",
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Failed, result.Status);
        Assert.Contains("no usable email address", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultPreferenceIsEmailOn()
    {
        // The two states that notify are the two where Charter is blocked on the person. A default
        // of silence means a request sits in NeedsInput until somebody happens to open the app.
        var preference = NotificationPreference.Default(Guid.NewGuid());

        Assert.True(preference.WantsAnything);
        Assert.Equal(NotificationChannel.Email, Assert.Single(preference.Channels));
    }

    [Fact]
    public void LabelsMatchTheStateMachineTable()
    {
        Assert.Equal("Question for you", NotifyWorthyStates.Label(RequestStatus.NeedsInput));
        Assert.Equal("Ready to try", NotifyWorthyStates.Label(RequestStatus.PreviewReady));
        Assert.Throws<ArgumentOutOfRangeException>(() => NotifyWorthyStates.Label(RequestStatus.Merged));
    }

    private static NotificationService Service(params INotificationChannel[] channels)
        => new(
            channels,
            new FixedNotificationPreferenceStore(NotificationChannel.Email),
            new RecordingLogger<NotificationService>());

    private static RequestNotification Notification(RequestStatus status) => new()
    {
        RequestId = Guid.CreateVersion7(),
        Status = status,
        Recipient = new NotificationRecipient
        {
            UserId = Guid.CreateVersion7(),
            Email = "person@example.com",
            DisplayName = "Sam",
        },
        RequestSummary = "Let customers download their invoices",
        Question = "Should paid invoices be included?",
        WhatToCheck = ["Open an old invoice and download it"],
        ThreadUrl = new Uri("https://charter.example.com/requests/1234"),
    };
}
