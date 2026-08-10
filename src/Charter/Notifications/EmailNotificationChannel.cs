using Charter.Domain;

namespace Charter.Notifications;

/// <summary>
/// Section 22's email channel: the one implementation behind <see cref="INotificationChannel"/>.
/// </summary>
/// <remarks>
/// Slack and Discord are named in section 22 and in <see cref="NotificationChannel"/>, and neither
/// ships here. A second channel is a second class implementing this interface plus a registration -
/// the fan-out, the preference lookup and the section 6 gate are all above it and do not move.
/// </remarks>
public sealed class EmailNotificationChannel : INotificationChannel
{
    private readonly IEmailSender sender;

    /// <summary>Creates the channel.</summary>
    public EmailNotificationChannel(IEmailSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        this.sender = sender;
    }

    /// <inheritdoc />
    public NotificationChannel Channel => NotificationChannel.Email;

    /// <inheritdoc />
    public bool IsAvailable => sender.Availability.Enabled;

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        RequestNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!EmailAddress.TryCreate(
                notification.Recipient.Email,
                notification.Recipient.DisplayName,
                out var to) || to is null)
        {
            // Not a mail server problem, and not something a retry fixes.
            return EmailDeliveryResult.Failed(
                "Charter has no usable email address for this person, so it could not tell them.");
        }

        var (content, kind) = Compose(notification);

        return await sender.SendAsync(
            new EmailMessage
            {
                To = to,
                Content = content,
                Category = EmailCategory.Notification,
                Kind = kind,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static (EmailContent Content, string Kind) Compose(RequestNotification notification)
        => notification.Status switch
        {
            RequestStatus.NeedsInput => (
                EmailTemplates.QuestionForYou(new QuestionForYouEmail
                {
                    RecipientName = notification.Recipient.DisplayName,
                    RequestSummary = notification.RequestSummary,
                    Question = notification.Question ?? string.Empty,
                    ThreadUrl = notification.ThreadUrl,
                    NotificationSettingsUrl = notification.NotificationSettingsUrl,
                }),
                EmailTemplates.NeedsInputKind),

            RequestStatus.PreviewReady => (
                EmailTemplates.ReadyToTry(new ReadyToTryEmail
                {
                    RecipientName = notification.Recipient.DisplayName,
                    RequestSummary = notification.RequestSummary,
                    WhatToCheck = notification.WhatToCheck,
                    ThreadUrl = notification.ThreadUrl,
                    NotificationSettingsUrl = notification.NotificationSettingsUrl,
                }),
                EmailTemplates.PreviewReadyKind),

            // Unreachable through INotificationService, which gates on section 6 first. Kept as a
            // throw rather than a default template so that adding a third notify-worthy state fails
            // loudly here instead of silently sending the wrong words.
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.Status,
                "Only NeedsInput and PreviewReady notify (section 6)."),
        };
}
