using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Data.Notifications;

/// <summary>
/// The record of attempted sends (change spec 001 C.3), in Postgres.
/// </summary>
/// <remarks>
/// <para>
/// C.3's point is that a delivery failure is <em>visible</em>. The in-memory ring satisfied that
/// until the container restarted, and the agent that wrote it said so rather than papering over it —
/// which meant the settings page went blank at exactly the moment an operator most wanted it, since
/// a redeploy is the usual response to "mail seems broken". So the log is a table.
/// </para>
/// <para>
/// It is pruned on both axes, because an unbounded record of every email an instance ever sent is
/// its own problem — it grows without limit, and it is a standing list of who was contacted and
/// when. <see cref="Retention"/> bounds it by age and <see cref="MaxRecords"/> by count; the sweep
/// rides along with a write rather than needing a scheduled job.
/// </para>
/// <para>
/// The interface is synchronous, so this is too. A write is one insert on the path of a mail that
/// has already spoken to an SMTP server, and a failure to <em>record</em> a send must never fail the
/// send: writes log and swallow. Reads do not — an admin page that showed an empty list because the
/// query failed would read as "no mail has ever been sent", which is the one wrong answer.
/// </para>
/// </remarks>
public sealed class EfEmailDeliveryLog : IEmailDeliveryLog
{
    /// <summary>How long an attempt is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>The hard ceiling, whatever the retention window says. A burst cannot outgrow it.</summary>
    public const int MaxRecords = 2000;

    /// <summary>How often the sweep runs, at most. Pruning on every send would double the writes.</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<EfEmailDeliveryLog> _logger;
    private readonly Lock _gate = new();

    private DateTimeOffset _lastPrunedAt = DateTimeOffset.MinValue;

    /// <summary>Creates the log.</summary>
    public EfEmailDeliveryLog(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<EfEmailDeliveryLog> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public EmailDeliveryRecord? LastFailure
    {
        get
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

            // Served by ix_email_deliveries_failed_at, so finding the last failure never walks the
            // successful sends in front of it.
            var failure = db.EmailDeliveries
                .AsNoTracking()
                .Where(delivery => delivery.Outcome == EmailDeliveryOutcome.Failed)
                .OrderByDescending(delivery => delivery.At)
                .FirstOrDefault();

            return failure is null ? null : Project(failure);
        }
    }

    /// <inheritdoc />
    public void Record(EmailDeliveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

            db.EmailDeliveries.Add(EmailDelivery.Record(
                record.At,
                record.Recipient,
                record.Kind,
                ToOutcome(record.Status),
                record.Summary,
                record.Detail));

            _ = db.SaveChanges();

            if (ShouldPrune())
            {
                _ = Prune(db);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording an attempt must never break the attempt. An invitation that was accepted by
            // the mail server and then failed to be written down is a gap in a diagnostic list; an
            // invitation that 500s because the diagnostic list was unavailable is a person who
            // cannot join.
            _logger.LogError(
                ex,
                "Could not record a {Kind} email delivery in the log. The send itself was not affected.",
                record.Kind);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EmailDeliveryRecord> Recent(int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        return
        [
            .. db.EmailDeliveries
                .AsNoTracking()
                .OrderByDescending(delivery => delivery.At)
                .Take(limit)
                .AsEnumerable()
                .Select(Project),
        ];
    }

    /// <summary>
    /// Applies retention now, and returns how many rows went.
    /// </summary>
    /// <remarks>Public so retention is testable as behaviour rather than as an interval that expires.</remarks>
    public int Prune()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        return Prune(db);
    }

    private int Prune(CharterDbContext db)
    {
        var cutoff = _clock.GetUtcNow() - Retention;

        var removed = db.EmailDeliveries
            .Where(delivery => delivery.At < cutoff)
            .ExecuteDelete();

        // And the count ceiling, for an instance that sends more in a month than anybody would page
        // through. The oldest kept row's timestamp is the fence; anything at or before it goes.
        var fence = db.EmailDeliveries
            .OrderByDescending(delivery => delivery.At)
            .Skip(MaxRecords)
            .Select(delivery => (DateTimeOffset?)delivery.At)
            .FirstOrDefault();

        if (fence is not null)
        {
            removed += db.EmailDeliveries
                .Where(delivery => delivery.At <= fence)
                .ExecuteDelete();
        }

        return removed;
    }

    private bool ShouldPrune()
    {
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            if (now - _lastPrunedAt < PruneInterval)
            {
                return false;
            }

            _lastPrunedAt = now;
            return true;
        }
    }

    private static EmailDeliveryRecord Project(EmailDelivery delivery) => new()
    {
        At = delivery.At,
        Recipient = delivery.Recipient,
        Kind = delivery.Kind,
        Status = ToStatus(delivery.Outcome),
        Summary = delivery.Summary,
        Detail = delivery.Detail,
    };

    // CS8524 is the unnamed-value arm. No default on either switch, deliberately: a new delivery
    // status must be a compile error here rather than a row that reads back as something else.
#pragma warning disable CS8524

    internal static EmailDeliveryOutcome ToOutcome(EmailDeliveryStatus status) => status switch
    {
        EmailDeliveryStatus.Sent => EmailDeliveryOutcome.Sent,
        EmailDeliveryStatus.Skipped => EmailDeliveryOutcome.Skipped,
        EmailDeliveryStatus.RateLimited => EmailDeliveryOutcome.RateLimited,
        EmailDeliveryStatus.Failed => EmailDeliveryOutcome.Failed,
    };

    internal static EmailDeliveryStatus ToStatus(EmailDeliveryOutcome outcome) => outcome switch
    {
        EmailDeliveryOutcome.Sent => EmailDeliveryStatus.Sent,
        EmailDeliveryOutcome.Skipped => EmailDeliveryStatus.Skipped,
        EmailDeliveryOutcome.RateLimited => EmailDeliveryStatus.RateLimited,
        EmailDeliveryOutcome.Failed => EmailDeliveryStatus.Failed,
    };

#pragma warning restore CS8524
}
