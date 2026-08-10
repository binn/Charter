namespace Charter.Api.Contracts;

/// <summary>One attempted delivery, as admin settings shows it (change spec 001 part C.3).</summary>
public sealed record EmailDeliveryResponse
{
    public required DateTimeOffset At { get; init; }

    /// <summary>Who it was for.</summary>
    public required string Recipient { get; init; }

    /// <summary>The template: <c>invitation</c>, <c>needs_input</c>, and so on.</summary>
    public required string Kind { get; init; }

    public required ApiEmailDeliveryStatus Status { get; init; }

    /// <summary>A sentence for the administrator.</summary>
    public required string Summary { get; init; }

    /// <summary>The mail server's own words, when it gave any. Shown under a disclosure.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// <c>GET /api/settings/email</c>: whether Charter can send mail, and what it has recently tried.
/// </summary>
/// <remarks>
/// Change spec 001 C.3's argument for this screen is that email misconfiguration is otherwise
/// discovered when an invitation silently fails and a new hire cannot sign in. So the availability
/// answer carries <see cref="DisabledReason"/> and <see cref="HowToEnable"/> — the variables to set —
/// rather than a bare <c>false</c>, and the recent log is present whether or not anything failed.
/// </remarks>
public sealed record EmailSettingsResponse
{
    /// <summary>True when Charter can send mail.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The configured provider token: <c>smtp</c> or <c>none</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The address mail is sent as. Absent when there is none.</summary>
    public string? FromAddress { get; init; }

    /// <summary>Why email is off, in plain language. Absent when it is on.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>What an operator would change to turn it on. Absent when it is on.</summary>
    public string? HowToEnable { get; init; }

    /// <summary>The most recent attempts, newest first.</summary>
    public required IReadOnlyList<EmailDeliveryResponse> Recent { get; init; }

    /// <summary>The most recent failure. What the page leads with; absent when there has been none.</summary>
    public EmailDeliveryResponse? LastFailure { get; init; }

    /// <summary>
    /// True when the delivery log above is in memory only, and therefore empty after a restart.
    /// </summary>
    /// <remarks>
    /// Stated rather than hidden: an admin looking at an empty list needs to know whether that means
    /// "nothing has been sent" or "this instance restarted".
    /// </remarks>
    public required bool RecentIsInMemory { get; init; }
}

/// <summary><c>POST /api/settings/email/test</c>.</summary>
public sealed record SendTestEmailBody
{
    /// <summary>Where to send it. Defaults to the signed-in administrator's own address.</summary>
    public string? Recipient { get; init; }
}

/// <summary>
/// What the settings page shows after somebody presses <em>Send a test email</em>.
/// </summary>
/// <remarks>
/// This endpoint never returns a failure status for a mail problem. A misconfigured server is
/// answered <c>200</c> with <see cref="Sent"/> false and the server's own words in
/// <see cref="Detail"/>, because the whole point of the button is to turn a silent
/// misconfiguration into a sentence somebody can act on — and a 500 with a stack trace is neither
/// (section 11).
/// </remarks>
public sealed record EmailTestResponse
{
    public required bool Sent { get; init; }

    /// <summary>One sentence for the person who pressed the button.</summary>
    public required string Message { get; init; }

    /// <summary>The mail server's own words, when it gave any.</summary>
    public string? Detail { get; init; }

    /// <summary>Where the message went. Absent when the address was not usable.</summary>
    public string? Recipient { get; init; }
}
