using System.Globalization;
using System.Text;

namespace Charter.Notifications;

/// <summary>
/// Renders an <see cref="EmailMessage"/> as the RFC 5322 document an SMTP <c>DATA</c> command
/// carries.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than taken from <c>System.Net.Mail</c>, because <c>SmtpClient</c> cannot do
/// implicit TLS - it only knows STARTTLS - and change spec 001 C.2 lists implicit as one of the
/// three TLS modes. Once the connection is ours, the message has to be ours too.
/// </para>
/// <para>
/// Everything is base64 with hard-wrapped lines. Quoted-printable produces smaller messages, but
/// base64 removes three whole classes of bug at once: no line can exceed the 998-octet limit, no
/// line can begin with a period and need dot-stuffing, and no byte sequence can be mangled by a
/// server that rewrites whitespace. Bandwidth is not the constraint on a password reset.
/// </para>
/// </remarks>
internal static class MimeWriter
{
    private const int Base64LineLength = 76;

    /// <summary>Builds the full message, headers and both bodies.</summary>
    internal static string Write(
        EmailMessage message,
        EmailAddress from,
        string? replyTo,
        DateTimeOffset sentAt,
        string boundary,
        string messageId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(from);

        var builder = new StringBuilder();

        AppendHeader(builder, "From", from.ToMailbox());
        AppendHeader(builder, "To", message.To.ToMailbox());

        if (replyTo is not null)
        {
            AppendHeader(builder, "Reply-To", replyTo);
        }

        AppendHeader(builder, "Subject", EncodeHeaderValue(message.Content.Subject));
        AppendHeader(builder, "Date", sentAt.ToString("r", CultureInfo.InvariantCulture));
        AppendHeader(builder, "Message-ID", $"<{messageId}>");
        AppendHeader(builder, "MIME-Version", "1.0");

        // Section 22 mail is not marketing, but mailbox providers cannot tell the difference, and an
        // auto-reply loop between a shared inbox and a notification is a genuinely bad afternoon.
        AppendHeader(builder, "Auto-Submitted", "auto-generated");
        AppendHeader(builder, "X-Auto-Response-Suppress", "All");
        AppendHeader(builder, "Content-Type", $"multipart/alternative; boundary=\"{boundary}\"");

        builder.Append("\r\n");
        builder.Append("This is a message in MIME format.\r\n");

        AppendPart(builder, boundary, "text/plain; charset=utf-8", message.Content.Text);
        AppendPart(builder, boundary, "text/html; charset=utf-8", message.Content.Html);

        builder.Append("--").Append(boundary).Append("--\r\n");

        return builder.ToString();
    }

    /// <summary>A boundary that cannot occur inside base64 content.</summary>
    internal static string NewBoundary() => $"--=_charter_{Guid.NewGuid():n}";

    /// <summary>
    /// RFC 2047 encoding, applied only when the value is not plain ASCII.
    /// </summary>
    /// <remarks>
    /// A subject is user-influenced text - a request title reaches it - so it cannot be trusted to
    /// be ASCII, and an unencoded high byte in a header is how a message ends up unreadable in one
    /// client and fine in another.
    /// </remarks>
    internal static string EncodeHeaderValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var single = SingleLine(value);

        return single.All(character => character is >= (char)32 and < (char)127)
            ? single
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(single))}?=";
    }

    /// <summary>
    /// Collapses every control character to a space.
    /// </summary>
    /// <remarks>
    /// This is the header injection control. A carriage return inside a subject or a display name
    /// would end the header and let the rest of the value be read as new headers - an extra
    /// <c>Bcc</c>, or a second body. Recipients are validated at construction; this catches the
    /// values that are not addresses.
    /// </remarks>
    internal static string SingleLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static void AppendHeader(StringBuilder builder, string name, string value)
        => builder.Append(name).Append(": ").Append(SingleLine(value)).Append("\r\n");

    private static void AppendPart(StringBuilder builder, string boundary, string contentType, string body)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Normalize(body)));
        for (var offset = 0; offset < encoded.Length; offset += Base64LineLength)
        {
            var length = Math.Min(Base64LineLength, encoded.Length - offset);
            builder.Append(encoded, offset, length).Append("\r\n");
        }

        builder.Append("\r\n");
    }

    /// <summary>Normalises line endings to CRLF, which is the only thing SMTP accepts.</summary>
    private static string Normalize(string body)
        => body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
}
