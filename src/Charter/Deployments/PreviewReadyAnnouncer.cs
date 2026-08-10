using System.Text.Json;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>What announcing a preview did.</summary>
/// <param name="Moved">True when the request and session were moved to <c>PreviewReady</c> here.</param>
/// <param name="Notification">What the notification service decided, or null when nothing was sent.</param>
public sealed record PreviewAnnouncement(bool Moved, NotificationOutcome? Notification);

/// <summary>
/// Section 6's second notifying state, and the only place it is entered.
/// </summary>
/// <remarks>
/// <para>
/// <c>PreviewReady</c> existed in the enum, the label table and the projection, and nothing ever set
/// it — so the one moment the requester is actually waiting for, <em>Ready to try</em>, never
/// happened. It belongs here because the state is a fact about a URL that answers, and this module is
/// the only one that learns that: the webhook, the poll and the comment parser all converge on
/// <see cref="PreviewArtifactPublisher"/> marking the artifact <c>Ready</c>.
/// </para>
/// <para>
/// The section 6 gate is <em>not</em> re-implemented here. <see cref="INotificationService"/> checks
/// it once, above the channels, and answers <c>NotNotifyWorthy</c> for everything else; a second copy
/// of the rule in a call site is how the closed set of two quietly becomes three.
/// </para>
/// <para>
/// Nothing the requester is sent carries a repository, a branch, a commit or a session id. That is a
/// property of <see cref="RequestNotification"/> rather than of care taken here, which is the point —
/// the payload has nowhere to put them.
/// </para>
/// </remarks>
public sealed class PreviewReadyAnnouncer
{
    private readonly CharterDbContext _database;
    private readonly DeploymentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreviewReadyAnnouncer> _logger;
    private readonly INotificationService? _notifications;

    public PreviewReadyAnnouncer(
        CharterDbContext database,
        DeploymentOptions options,
        TimeProvider clock,
        ILogger<PreviewReadyAnnouncer> logger,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _clock = clock;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Moves the session and its request to <c>PreviewReady</c> and tells the requester.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the thing that calls it is a reconcile loop. A request already sitting in
    /// <c>PreviewReady</c> is not moved again and nobody is told twice — a preview that redeploys is
    /// the same news, and section 6's whole argument for two notifying states is that Charter gets
    /// muted the moment it is noisy.
    /// </remarks>
    public async Task<PreviewAnnouncement> AnnounceAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _database.Sessions.FirstOrDefaultAsync(
            row => row.Id == sessionId,
            cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return new PreviewAnnouncement(false, null);
        }

        // Read-only first, then load the one row that is written as a tracked entity. A projection
        // built over an AsNoTracking root hands back detached instances, and a detached entity that
        // is transitioned saves nothing at all — silently.
        var context = await (from spec in _database.Specs.AsNoTracking()
                             where spec.Id == session.SpecId
                             join filed in _database.Requests.AsNoTracking()
                                 on spec.RequestId equals filed.Id
                             join user in _database.Users.AsNoTracking()
                                 on filed.RequesterId equals user.Id
                             select new { Spec = spec, RequestId = filed.Id, User = user })
            .FirstOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            return new PreviewAnnouncement(false, null);
        }

        var request = await _database.Requests.FirstOrDefaultAsync(
            row => row.Id == context.RequestId,
            cancellationToken);

        if (request is null)
        {
            return new PreviewAnnouncement(false, null);
        }

        var alreadyThere = request.Status == RequestStatus.PreviewReady;
        var now = _clock.GetUtcNow();
        var moved = false;

        if (session.Status != SessionStatus.PreviewReady)
        {
            session.TransitionTo(SessionStatus.PreviewReady, now);
            moved = true;
        }

        // Section 6 puts PreviewReady after PROpen and before InReview. A request that has moved past
        // it — an engineer is already reviewing, or it is merged — is not dragged backwards because a
        // preview redeployed.
        if (request.Status is RequestStatus.Queued
            or RequestStatus.Running
            or RequestStatus.NeedsInput
            or RequestStatus.PrOpen)
        {
            request.TransitionTo(RequestStatus.PreviewReady, now);
            moved = true;
        }

        if (moved)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        if (alreadyThere || _notifications is null)
        {
            return new PreviewAnnouncement(moved, null);
        }

        var outcome = await NotifyAsync(request, context.Spec, context.User, cancellationToken);

        return new PreviewAnnouncement(moved, outcome);
    }

    private async Task<NotificationOutcome?> NotifyAsync(
        Request request,
        Spec spec,
        User requester,
        CancellationToken cancellationToken)
    {
        var notification = new RequestNotification
        {
            RequestId = request.Id,
            Status = RequestStatus.PreviewReady,
            Recipient = new NotificationRecipient
            {
                UserId = requester.Id,
                Email = requester.Email,
                DisplayName = requester.DisplayName,
            },
            RequestSummary = request.RawText,
            ThreadUrl = _options.ThreadUrlFor(request.Id),

            // Section 11: "what to check" is the approved acceptance criteria, verbatim. Without it a
            // preview URL is a dead end, and regenerating it would mean the mail and the card
            // disagreed about the contract the requester approved.
            WhatToCheck = AcceptanceCriteria(spec),
            NotificationSettingsUrl = _options.NotificationSettingsUrl,
        };

        try
        {
            var outcome = await _notifications!.NotifyAsync(notification, cancellationToken);

            _logger.LogInformation(
                "Request {RequestId} reached PreviewReady; notification {Outcome}",
                request.Id,
                outcome.Kind);

            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A state transition must not roll back because a mail server was down. The row already
            // says PreviewReady and the thread already shows it.
            _logger.LogError(
                exception,
                "Could not notify the requester that request {RequestId} is ready to try",
                request.Id);

            return null;
        }
    }

    private static IReadOnlyList<string> AcceptanceCriteria(Spec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.AcceptanceCriteria))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(spec.AcceptanceCriteria) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
