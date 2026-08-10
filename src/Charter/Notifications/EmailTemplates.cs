using System.Text;
using System.Text.Encodings.Web;

namespace Charter.Notifications;

/// <summary>Somebody has been invited to this instance (sections 30.2, 7.2a).</summary>
public sealed record InvitationEmail
{
    /// <summary>The person doing the inviting, as they are named in the app.</summary>
    public required string InviterName { get; init; }

    /// <summary>The organisation. One per instance (section 7.2a).</summary>
    public required string OrganizationName { get; init; }

    /// <summary>The one-time link that creates the account.</summary>
    public required Uri AcceptUrl { get; init; }

    /// <summary>When the link stops working, if it does.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Somebody asked to reset their password.</summary>
public sealed record PasswordResetEmail
{
    /// <summary>Who the account belongs to. Null when there is no name on it yet.</summary>
    public string? RecipientName { get; init; }

    /// <summary>The one-time link.</summary>
    public required Uri ResetUrl { get; init; }

    /// <summary>How long the link is good for.</summary>
    public required TimeSpan ValidFor { get; init; }
}

/// <summary>
/// <c>NeedsInput</c>, as the requester reads it: <em>Question for you</em> (section 6).
/// </summary>
/// <remarks>
/// There is no field here for a repository, a branch, a commit or a session. Section 7.1 says a
/// requester never sees any of them, and the cheapest way to keep that true in email is for the
/// template to have nowhere to put them.
/// </remarks>
public sealed record QuestionForYouEmail
{
    /// <summary>Who is being asked.</summary>
    public string? RecipientName { get; init; }

    /// <summary>What they asked for, in their own words.</summary>
    public required string RequestSummary { get; init; }

    /// <summary>The question itself.</summary>
    public required string Question { get; init; }

    /// <summary>The status thread. One thread per request, forever (section 11).</summary>
    public required Uri ThreadUrl { get; init; }

    /// <summary>Where this person turns these emails off (section 22).</summary>
    public Uri? NotificationSettingsUrl { get; init; }
}

/// <summary>
/// <c>PreviewReady</c>, as the requester reads it: <em>Ready to try</em> (section 6).
/// </summary>
public sealed record ReadyToTryEmail
{
    /// <summary>Who asked for it.</summary>
    public string? RecipientName { get; init; }

    /// <summary>What they asked for, in their own words.</summary>
    public required string RequestSummary { get; init; }

    /// <summary>
    /// What to check, from the acceptance criteria. Section 11: without it a preview URL is a dead
    /// end.
    /// </summary>
    public IReadOnlyList<string> WhatToCheck { get; init; } = [];

    /// <summary>The status thread, where the two feedback buttons are.</summary>
    public required Uri ThreadUrl { get; init; }

    /// <summary>Where this person turns these emails off (section 22).</summary>
    public Uri? NotificationSettingsUrl { get; init; }
}

/// <summary>
/// Every template Charter sends, rendered as HTML and plain text (change spec 001, part C.3).
/// </summary>
/// <remarks>
/// <para>
/// This is a product surface, not boilerplate. The recipient is often the non-engineer of section
/// 7.1, and section 11 governs the copy: plain language, no stack traces, no repository names, no
/// jargon that assumes the reader knows what a branch is. Two rules from elsewhere in the spec show
/// up as constraints here - no ETA anywhere, ever (section 6), and no metric a requester cannot act
/// on.
/// </para>
/// <para>
/// The HTML is one column of inline styles with no images, no web fonts and no external stylesheet.
/// Mail clients strip most of what a modern page relies on, and a template that needs a CDN to be
/// legible is a template that is illegible in Outlook and in every client with images off.
/// </para>
/// </remarks>
public static class EmailTemplates
{
    /// <summary>Kind labels, for the delivery log and for metrics.</summary>
    public const string InvitationKind = "invitation";

    /// <inheritdoc cref="InvitationKind" />
    public const string PasswordResetKind = "password_reset";

    /// <inheritdoc cref="InvitationKind" />
    public const string NeedsInputKind = "needs_input";

    /// <inheritdoc cref="InvitationKind" />
    public const string PreviewReadyKind = "preview_ready";

    /// <inheritdoc cref="InvitationKind" />
    public const string TestKind = "test";

    /// <summary>The invitation of section 30.2.</summary>
    public static EmailContent Invitation(InvitationEmail model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var inviter = RequesterSafeText.OwnWords(model.InviterName, 80);
        var organization = RequesterSafeText.OwnWords(model.OrganizationName, 80);

        var expiry = model.ExpiresAt is { } expiresAt
            ? $"This link works until {expiresAt.UtcDateTime:d MMMM yyyy, HH:mm} UTC."
            : "This link can only be used once.";

        var body = new List<string>
        {
            $"{inviter} has invited you to join {organization} on Charter.",
            "Charter is where you ask for changes to your product in plain English and see them " +
            "working before anyone else does. You do not need to know how any of it is built.",
            expiry,
        };

        return Render(
            subject: $"{inviter} invited you to {organization}",
            preheader: "Set up your account and start asking for changes.",
            heading: "You have been invited",
            paragraphs: body,
            action: new EmailAction("Set up your account", model.AcceptUrl),
            footer: "If you were not expecting this, you can ignore it and nothing will happen.");
    }

    /// <summary>A password reset, one of the four things email is needed for (change spec 001 C.1).</summary>
    public static EmailContent PasswordReset(PasswordResetEmail model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var greeting = Greeting(model.RecipientName);

        return Render(
            subject: "Reset your Charter password",
            preheader: "A link to choose a new password.",
            heading: "Choose a new password",
            paragraphs:
            [
                $"{greeting}somebody asked to reset the password on your Charter account.",
                $"The link below works once, and only for the next {Describe(model.ValidFor)}.",
            ],
            action: new EmailAction("Choose a new password", model.ResetUrl),
            footer: "If this was not you, ignore this email. Your password stays as it is, and " +
                    "nobody can use the link without this message.");
    }

    /// <summary>
    /// <c>NeedsInput</c>. One of the two states that notify (section 6).
    /// </summary>
    public static EmailContent QuestionForYou(QuestionForYouEmail model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var summary = RequesterSafeText.OwnWords(model.RequestSummary, 160);
        var question = RequesterSafeText.Scrub(model.Question);
        var greeting = Greeting(model.RecipientName);

        return Render(
            subject: "A question about your request",
            preheader: question.Length > 0 ? question : "There is one thing to check with you.",
            heading: "Question for you",
            paragraphs:
            [
                $"{greeting}work on “{summary}” has paused on one question.",
                question,
                "Answering it is the only thing needed to carry on.",
            ],
            action: new EmailAction("Answer the question", model.ThreadUrl),
            footer: NotificationFooter(model.NotificationSettingsUrl));
    }

    /// <summary>
    /// <c>PreviewReady</c>. The second and last state that notifies (section 6).
    /// </summary>
    public static EmailContent ReadyToTry(ReadyToTryEmail model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var summary = RequesterSafeText.OwnWords(model.RequestSummary, 160);
        var greeting = Greeting(model.RecipientName);

        var paragraphs = new List<string>
        {
            $"{greeting}“{summary}” is ready for you to look at.",
        };

        var checks = model.WhatToCheck
            .Select(item => RequesterSafeText.Scrub(item, 200))
            .Where(item => item.Length > 0)
            .Take(8)
            .ToList();

        if (checks.Count > 0)
        {
            paragraphs.Add("What to check:");
        }

        paragraphs.Add(
            "When you have had a look, tell us whether it works or not - there are two buttons on " +
            "the page, and “Not quite” is just as useful as “Works”.");

        return Render(
            subject: "Ready to try",
            preheader: $"“{summary}” is ready for you to look at.",
            heading: "Ready to try",
            paragraphs: paragraphs,
            action: new EmailAction("Open it", model.ThreadUrl),
            footer: NotificationFooter(model.NotificationSettingsUrl),
            bullets: checks,
            bulletsAfterParagraph: checks.Count > 0 ? 1 : -1);
    }

    /// <summary>
    /// The send-a-test-email of change spec 001 C.3.
    /// </summary>
    /// <remarks>
    /// Deliberately dull, and deliberately explicit about what its arrival proves. The whole point
    /// is that email misconfiguration is otherwise discovered when an invitation silently fails and
    /// a new hire cannot log in.
    /// </remarks>
    public static EmailContent Test(string instanceName, DateTimeOffset sentAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        return Render(
            subject: "Charter test email",
            preheader: "Email is working on this Charter instance.",
            heading: "Email is working",
            paragraphs:
            [
                $"This is a test message from {RequesterSafeText.OwnWords(instanceName, 80)}, sent at " +
                $"{sentAt.UtcDateTime:d MMMM yyyy, HH:mm} UTC.",
                "If you are reading this, invitations, password resets and status notifications can " +
                "all be delivered.",
            ],
            action: null,
            footer: "Sent from the email settings page. Nobody else received it.");
    }

    private static string Greeting(string? name)
    {
        var cleaned = RequesterSafeText.OwnWords(name, 60);
        return cleaned.Length == 0 ? string.Empty : $"{cleaned}, ";
    }

    private static string NotificationFooter(Uri? settingsUrl)
        => settingsUrl is null
            ? "Charter only emails you about the two things that need you: a question, and something " +
              "ready to try."
            : "Charter only emails you about the two things that need you: a question, and something " +
              $"ready to try. You can change that at {settingsUrl}.";

    private static string Describe(TimeSpan validFor)
        => validFor.TotalHours >= 2
            ? $"{validFor.TotalHours:0} hours"
            : validFor.TotalMinutes >= 2
                ? $"{validFor.TotalMinutes:0} minutes"
                : "few minutes";

    /// <summary>One call to action. At most one per message: two buttons is no button.</summary>
    private sealed record EmailAction(string Label, Uri Url);

    /// <summary>
    /// Builds both renderings from one structure, so they cannot drift.
    /// </summary>
    /// <remarks>
    /// Writing the plain-text body by hand beside the HTML is how the two end up saying different
    /// things six months apart, and the plain-text one is the version that reaches a screen reader,
    /// a watch and a client with HTML turned off.
    /// </remarks>
    private static EmailContent Render(
        string subject,
        string preheader,
        string heading,
        IReadOnlyList<string> paragraphs,
        EmailAction? action,
        string footer,
        IReadOnlyList<string>? bullets = null,
        int bulletsAfterParagraph = -1)
    {
        var visible = paragraphs.Where(paragraph => paragraph.Length > 0).ToList();

        return new EmailContent
        {
            Subject = MimeWriter.SingleLine(subject),
            Html = RenderHtml(preheader, heading, visible, action, footer, bullets, bulletsAfterParagraph),
            Text = RenderText(heading, visible, action, footer, bullets, bulletsAfterParagraph),
        };
    }

    private static string RenderHtml(
        string preheader,
        string heading,
        IReadOnlyList<string> paragraphs,
        EmailAction? action,
        string footer,
        IReadOnlyList<string>? bullets,
        int bulletsAfterParagraph)
    {
        const string bodyStyle =
            "margin:0;padding:24px;background-color:#f6f7f9;" +
            "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;" +
            "color:#1f2328;line-height:1.55;";

        const string cardStyle =
            "max-width:560px;margin:0 auto;padding:32px;background-color:#ffffff;" +
            "border:1px solid #e3e6ea;border-radius:12px;";

        const string buttonStyle =
            "display:inline-block;padding:12px 20px;background-color:#1f2328;color:#ffffff;" +
            "text-decoration:none;border-radius:8px;font-weight:600;";

        var html = new StringBuilder();

        html.Append("<div style=\"").Append(bodyStyle).Append("\">");

        // The preview line every client shows beside the subject, hidden inside the message itself.
        html.Append("<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">")
            .Append(Escape(preheader))
            .Append("</div>");

        html.Append("<div style=\"").Append(cardStyle).Append("\">");
        html.Append("<h1 style=\"margin:0 0 16px;font-size:20px;line-height:1.3;\">")
            .Append(Escape(heading))
            .Append("</h1>");

        for (var index = 0; index < paragraphs.Count; index++)
        {
            html.Append("<p style=\"margin:0 0 16px;font-size:15px;\">")
                .Append(Escape(paragraphs[index]))
                .Append("</p>");

            if (index == bulletsAfterParagraph && bullets is { Count: > 0 })
            {
                html.Append("<ul style=\"margin:0 0 16px;padding-left:20px;font-size:15px;\">");
                foreach (var bullet in bullets)
                {
                    html.Append("<li style=\"margin:0 0 6px;\">").Append(Escape(bullet)).Append("</li>");
                }

                html.Append("</ul>");
            }
        }

        if (action is not null)
        {
            html.Append("<p style=\"margin:24px 0;\"><a href=\"")
                .Append(Escape(action.Url.ToString()))
                .Append("\" style=\"")
                .Append(buttonStyle)
                .Append("\">")
                .Append(Escape(action.Label))
                .Append("</a></p>");

            // Buttons do not survive every client, and a link nobody can copy is a dead end.
            html.Append("<p style=\"margin:0 0 16px;font-size:13px;color:#5a6169;\">")
                .Append("Or copy this link: ")
                .Append(Escape(action.Url.ToString()))
                .Append("</p>");
        }

        html.Append("<p style=\"margin:24px 0 0;padding-top:16px;border-top:1px solid #e3e6ea;")
            .Append("font-size:13px;color:#5a6169;\">")
            .Append(Escape(footer))
            .Append("</p>");

        html.Append("</div></div>");

        return html.ToString();
    }

    private static string RenderText(
        string heading,
        IReadOnlyList<string> paragraphs,
        EmailAction? action,
        string footer,
        IReadOnlyList<string>? bullets,
        int bulletsAfterParagraph)
    {
        var text = new StringBuilder();

        text.Append(heading).Append("\n\n");

        for (var index = 0; index < paragraphs.Count; index++)
        {
            text.Append(paragraphs[index]).Append("\n\n");

            if (index == bulletsAfterParagraph && bullets is { Count: > 0 })
            {
                foreach (var bullet in bullets)
                {
                    text.Append("  - ").Append(bullet).Append('\n');
                }

                text.Append('\n');
            }
        }

        if (action is not null)
        {
            text.Append(action.Label).Append(":\n").Append(action.Url).Append("\n\n");
        }

        text.Append("--\n").Append(footer).Append('\n');

        return text.ToString();
    }

    private static string Escape(string value) => HtmlEncoder.Default.Encode(value);
}
