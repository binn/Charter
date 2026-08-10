namespace Charter.Notifications;

/// <summary>What the settings page shows after somebody presses <em>Send a test email</em>.</summary>
public sealed record EmailTestResult
{
    /// <summary>True when the mail server took it.</summary>
    public required bool Sent { get; init; }

    /// <summary>One sentence for the person who pressed the button.</summary>
    public required string Message { get; init; }

    /// <summary>The mail server's own words, when it gave any. Shown under a disclosure.</summary>
    public string? Detail { get; init; }

    /// <summary>Where the message went.</summary>
    public string? Recipient { get; init; }
}

/// <summary>
/// The <em>send a test email</em> of change spec 001, part C.3.
/// </summary>
/// <remarks>
/// Change spec 001 calls this out on its own, and the reason it gives is the whole argument for it:
/// email misconfiguration is otherwise discovered when an invitation silently fails and a new hire
/// cannot log in. A button that produces a plain answer - it worked, or here is what the server
/// said - converts that from a support incident weeks later into thirty seconds during setup.
/// </remarks>
public interface IEmailTester
{
    /// <summary>Sends one test message to <paramref name="recipient"/>.</summary>
    Task<EmailTestResult> SendTestAsync(string recipient, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IEmailTester" />
public sealed class EmailTester : IEmailTester
{
    private readonly IEmailSender sender;
    private readonly TimeProvider clock;
    private readonly string instanceName;

    /// <summary>Creates the tester.</summary>
    /// <param name="sender">The same path a real message takes; a test that bypasses it proves nothing.</param>
    /// <param name="clock">Stamps the message, so a stale copy in an inbox is recognisable.</param>
    /// <param name="instanceName">What this instance calls itself, usually its base URL host.</param>
    public EmailTester(IEmailSender sender, TimeProvider clock, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        this.sender = sender;
        this.clock = clock;
        this.instanceName = instanceName;
    }

    /// <inheritdoc />
    public async Task<EmailTestResult> SendTestAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.TryCreate(recipient, displayName: null, out var to) || to is null)
        {
            return new EmailTestResult
            {
                Sent = false,
                Message = "That does not look like an email address. Enter one address, " +
                          "for example you@example.com.",
            };
        }

        if (!sender.Availability.Enabled)
        {
            // The one case where the answer is known before anything is attempted. Saying so is more
            // use than a failed send, because it names the variables to set.
            return new EmailTestResult
            {
                Sent = false,
                Recipient = to.Address,
                Message = sender.Availability.DisabledReason ?? NullEmailProvider.Explanation,
                Detail = sender.Availability.HowToEnable,
            };
        }

        var result = await sender.SendAsync(
            new EmailMessage
            {
                To = to,
                Content = EmailTemplates.Test(instanceName, clock.GetUtcNow()),
                Category = EmailCategory.Transactional,
                Kind = EmailTemplates.TestKind,
            },
            cancellationToken).ConfigureAwait(false);

        return new EmailTestResult
        {
            Sent = result.Delivered,
            Recipient = to.Address,
            Message = result.Delivered
                ? $"Test email sent to {to.Address}. If it does not arrive within a few minutes, " +
                  "check the spam folder and the sending domain's SPF and DKIM records."
                : result.Summary,
            Detail = result.Detail,
        };
    }
}
