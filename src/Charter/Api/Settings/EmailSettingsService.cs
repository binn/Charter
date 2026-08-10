using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Settings;

/// <summary>
/// <c>GET /api/settings/email</c> and <c>POST /api/settings/email/test</c> (change spec 001 part C.3).
/// </summary>
/// <remarks>
/// <para>
/// The argument for this screen is in the change spec: email misconfiguration is otherwise discovered
/// when an invitation silently fails and a new hire cannot sign in. So availability arrives with the
/// variables to change, the recent delivery log arrives whether or not anything failed, and the test
/// button answers in a sentence.
/// </para>
/// <para>
/// <strong>The test never throws.</strong> A misconfigured server answers <c>200</c> with
/// <c>sent: false</c> and the mail server's own words — because a 500 tells the admin that Charter
/// is broken when what is broken is their SMTP host, and section 11 has no time for a stack trace
/// reaching a person either way.
/// </para>
/// </remarks>
public sealed class EmailSettingsService
{
    private readonly CharterDbContext database;
    private readonly IEmailSender sender;
    private readonly IEmailTester tester;
    private readonly IEmailDeliveryLog log;

    public EmailSettingsService(
        CharterDbContext database,
        IEmailSender sender,
        IEmailTester tester,
        IEmailDeliveryLog log)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(log);

        this.database = database;
        this.sender = sender;
        this.tester = tester;
        this.log = log;
    }

    /// <summary>How many recent attempts the settings page shows.</summary>
    public const int RecentLimit = 25;

    /// <summary>Availability plus the recent delivery log. Admin only.</summary>
    public Task<(CommandOutcome Outcome, EmailSettingsResponse? Settings)> DescribeAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return Task.FromResult<(CommandOutcome, EmailSettingsResponse?)>(
                (CommandOutcome.Forbidden("email settings belong to administrators"), null));
        }

        var availability = sender.Availability;
        var recent = log.Recent(RecentLimit);

        return Task.FromResult<(CommandOutcome, EmailSettingsResponse?)>((
            CommandOutcome.Ok(),
            new EmailSettingsResponse
            {
                Enabled = availability.Enabled,
                Provider = availability.Provider,
                FromAddress = availability.FromAddress,
                DisabledReason = availability.DisabledReason,
                HowToEnable = availability.HowToEnable,
                Recent = [.. recent.Select(Describe)],
                LastFailure = log.LastFailure is { } failure ? Describe(failure) : null,

                // Stated rather than left to be inferred: the log is a bounded in-memory ring, so an
                // empty list after a restart means "we lost the record", not "nothing was sent".
                RecentIsInMemory = log is RecentEmailDeliveryLog,
            }));
    }

    /// <summary>
    /// Sends one test message. Admin only, and never a failure status for a mail problem.
    /// </summary>
    /// <param name="member">The acting admin.</param>
    /// <param name="body">Where to send it. Defaults to the admin's own address.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task<(CommandOutcome Outcome, EmailTestResponse? Result)> SendTestAsync(
        MemberSnapshot member,
        SendTestEmailBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (CommandOutcome.Forbidden("sending a test email is an administrator action"), null);
        }

        // Their own address by default. An admin checking whether mail works wants it in their own
        // inbox, and asking them to type an address they have already told Charter is friction.
        var recipient = body.Recipient?.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            recipient = await database.Users
                .AsNoTracking()
                .Where(row => row.Id == member.UserId)
                .Select(row => row.Email)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(recipient))
        {
            return (
                CommandOutcome.Ok(),
                new EmailTestResponse
                {
                    Sent = false,
                    Message = "Enter an address to send the test to.",
                });
        }

        EmailTestResult result;

        try
        {
            result = await tester.SendTestAsync(recipient, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The sender contract says it returns delivery problems rather than throwing, and a
            // transport that breaks that contract must not turn this button into a 500. The server's
            // own words go in `detail`, which is what an operator needs to fix it.
            return (
                CommandOutcome.Ok(),
                new EmailTestResponse
                {
                    Sent = false,
                    Recipient = recipient,
                    Message =
                        "Charter could not reach the mail server. Check CHARTER_SMTP_URL and that the "
                        + "host is reachable from this instance.",
                    Detail = exception.Message,
                });
        }

        return (
            CommandOutcome.Ok(),
            new EmailTestResponse
            {
                Sent = result.Sent,
                Message = result.Message,
                Detail = result.Detail,
                Recipient = result.Recipient,
            });
    }

    private static EmailDeliveryResponse Describe(EmailDeliveryRecord record) => new()
    {
        At = record.At,
        Recipient = record.Recipient,
        Kind = record.Kind,
        Status = record.Status switch
        {
            EmailDeliveryStatus.Sent => ApiEmailDeliveryStatus.Sent,
            EmailDeliveryStatus.Skipped => ApiEmailDeliveryStatus.Skipped,
            EmailDeliveryStatus.RateLimited => ApiEmailDeliveryStatus.RateLimited,
            EmailDeliveryStatus.Failed => ApiEmailDeliveryStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(record), record.Status, "Unknown delivery status."),
        },
        Summary = record.Summary,
        Detail = record.Detail,
    };
}
