using Charter.Domain;
using Microsoft.Extensions.Logging;

namespace Charter.Notifications;

/// <summary>Who a notification is for.</summary>
public sealed record NotificationRecipient
{
    /// <summary>The Charter user. Used to look up their channel preference.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Their email address.</summary>
    public required string Email { get; init; }

    /// <summary>Their display name, for the greeting.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// One request reaching one of the two notify-worthy states (sections 6, 22).
/// </summary>
/// <remarks>
/// The payload is requester-shaped by construction. There is no repository, no branch, no commit,
/// no session id and no cost on it - section 7.1 says a requester sees none of those, and a
/// notification that carried them would only be safe for as long as every channel remembered not to
/// render them.
/// </remarks>
public sealed record RequestNotification
{
    /// <summary>Which request. For correlation in logs, never shown.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>The state reached. Anything but the two notify-worthy ones is suppressed.</summary>
    public required RequestStatus Status { get; init; }

    /// <summary>Who to tell.</summary>
    public required NotificationRecipient Recipient { get; init; }

    /// <summary>What they asked for, in their own words.</summary>
    public required string RequestSummary { get; init; }

    /// <summary>The status thread for this request (section 11).</summary>
    public required Uri ThreadUrl { get; init; }

    /// <summary>The question, when the state is <see cref="RequestStatus.NeedsInput"/>.</summary>
    public string? Question { get; init; }

    /// <summary>
    /// What to check, when the state is <see cref="RequestStatus.PreviewReady"/>. Section 11:
    /// without it a preview URL is a dead end.
    /// </summary>
    public IReadOnlyList<string> WhatToCheck { get; init; } = [];

    /// <summary>Where the recipient changes their channel preference.</summary>
    public Uri? NotificationSettingsUrl { get; init; }
}

/// <summary>Why a notification did or did not go out.</summary>
public enum NotificationOutcomeKind
{
    /// <summary>At least one channel took it.</summary>
    Delivered,

    /// <summary>The state is not one of the two that notify (section 6).</summary>
    NotNotifyWorthy,

    /// <summary>The recipient has turned every channel off.</summary>
    OptedOut,

    /// <summary>Every channel the recipient wants is unavailable on this instance.</summary>
    NoChannelAvailable,

    /// <summary>Every channel tried and failed. Surfaced, never swallowed.</summary>
    Failed,
}

/// <summary>What happened on one channel.</summary>
public sealed record NotificationChannelResult(NotificationChannel Channel, EmailDeliveryResult Result);

/// <summary>The result of one notification attempt across every channel the recipient wants.</summary>
public sealed record NotificationOutcome
{
    /// <summary>The summary answer.</summary>
    public required NotificationOutcomeKind Kind { get; init; }

    /// <summary>Per-channel detail, in the order the channels were tried.</summary>
    public IReadOnlyList<NotificationChannelResult> Channels { get; init; } = [];

    /// <summary>True when something was actually sent.</summary>
    public bool Delivered => Kind is NotificationOutcomeKind.Delivered;
}

/// <summary>One channel a notification can be delivered on (section 22).</summary>
public interface INotificationChannel
{
    /// <summary>Which channel this is.</summary>
    NotificationChannel Channel { get; }

    /// <summary>False when the channel is not configured on this instance.</summary>
    bool IsAvailable { get; }

    /// <summary>Delivers one notification. Reports failure by returning it.</summary>
    Task<EmailDeliveryResult> SendAsync(
        RequestNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>The single outbound abstraction of section 22.</summary>
public interface INotificationService
{
    /// <summary>
    /// Notifies the recipient, if the state is one of the two that notify and they want it.
    /// </summary>
    Task<NotificationOutcome> NotifyAsync(
        RequestNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The fan-out: one state change in, zero or more channel sends out.
/// </summary>
/// <remarks>
/// <para>
/// The section 6 check happens here, once, rather than at each call site. A caller that has to
/// remember to ask <c>NotifyWorthyStates.Notifies</c> before calling is a caller that will
/// eventually forget, and the failure mode of forgetting is Charter emailing somebody about
/// <c>Queued</c>.
/// </para>
/// <para>
/// Nothing here throws for a delivery problem. Notifying is a side effect of a state transition, and
/// a state transition must not roll back because a mail server was down.
/// </para>
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private readonly IReadOnlyList<INotificationChannel> channels;
    private readonly INotificationPreferenceStore preferences;
    private readonly ILogger<NotificationService> logger;

    /// <summary>Creates the service over the registered channels.</summary>
    public NotificationService(
        IEnumerable<INotificationChannel> channels,
        INotificationPreferenceStore preferences,
        ILogger<NotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(logger);

        this.channels = [.. channels];
        this.preferences = preferences;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<NotificationOutcome> NotifyAsync(
        RequestNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!NotifyWorthyStates.Notifies(notification.Status))
        {
            // Debug, not warning. Most state transitions land here and that is the design.
            logger.LogDebug(
                "No notification for request {RequestId}: {Status} is not notify-worthy",
                notification.RequestId,
                notification.Status);

            return new NotificationOutcome { Kind = NotificationOutcomeKind.NotNotifyWorthy };
        }

        var preference = await preferences
            .GetAsync(notification.Recipient.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (!preference.WantsAnything)
        {
            return new NotificationOutcome { Kind = NotificationOutcomeKind.OptedOut };
        }

        var wanted = channels
            .Where(channel => preference.Channels.Contains(channel.Channel) && channel.IsAvailable)
            .ToList();

        if (wanted.Count == 0)
        {
            logger.LogInformation(
                "Request {RequestId} reached {Status} but no notification channel is available for the recipient",
                notification.RequestId,
                notification.Status);

            return new NotificationOutcome { Kind = NotificationOutcomeKind.NoChannelAvailable };
        }

        var results = new List<NotificationChannelResult>(wanted.Count);
        var delivered = false;

        foreach (var channel in wanted)
        {
            var result = await channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);
            results.Add(new NotificationChannelResult(channel.Channel, result));
            delivered |= result.Delivered;
        }

        var kind = delivered
            ? NotificationOutcomeKind.Delivered
            : results.Any(entry => entry.Result.IsFailure)
                ? NotificationOutcomeKind.Failed
                : NotificationOutcomeKind.NoChannelAvailable;

        if (kind is NotificationOutcomeKind.Failed)
        {
            logger.LogError(
                "Every channel failed to notify {UserId} that request {RequestId} reached {Status}",
                notification.Recipient.UserId,
                notification.RequestId,
                notification.Status);
        }

        return new NotificationOutcome { Kind = kind, Channels = results };
    }
}
