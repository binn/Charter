using System.Text;

namespace Charter.Notifications;

/// <summary>
/// What a message is for, which is what decides how hard the rate limiter squeezes it.
/// </summary>
/// <remarks>
/// Two buckets rather than one, because they fail in opposite directions. A notification storm is
/// the thing change spec 001 C.3 wants stopped; an invitation or a password reset arriving late is
/// the thing part C.1 says must never happen. Counting them together would let a burst of status
/// mail starve the message a new hire is waiting for.
/// </remarks>
public enum EmailCategory
{
    /// <summary>Invitations, password resets, the settings test send. Somebody is waiting for it.</summary>
    Transactional,

    /// <summary>Status mail from section 22. Useful, but nobody is blocked on it.</summary>
    Notification,
}

/// <summary>One recipient: an address, and optionally the name to show beside it.</summary>
/// <remarks>
/// Construction validates, so an address that could break the envelope or inject a second header
/// cannot reach the transport at all. <see cref="TryCreate"/> exists because most callers are
/// handling user-entered text and a thrown exception is the wrong shape for that.
/// </remarks>
public sealed record EmailAddress
{
    private EmailAddress(string address, string? displayName)
    {
        Address = address;
        DisplayName = displayName;
    }

    /// <summary>The address itself, lower-cased in the domain part by the sender that normalises it.</summary>
    public string Address { get; }

    /// <summary>The human name, or <c>null</c>. Never contains a control character.</summary>
    public string? DisplayName { get; }

    /// <summary>Parses <paramref name="address"/>, returning <c>false</c> rather than throwing.</summary>
    public static bool TryCreate(string? address, string? displayName, out EmailAddress? result)
    {
        result = null;

        var trimmed = address?.Trim();
        if (!Configuration.EmailConfig.IsDeliverableAddress(trimmed))
        {
            return false;
        }

        result = new EmailAddress(trimmed!, CleanDisplayName(displayName));
        return true;
    }

    /// <summary>Parses <paramref name="address"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="address"/> is not deliverable.</exception>
    public static EmailAddress Create(string address, string? displayName = null)
        => TryCreate(address, displayName, out var result) && result is not null
            ? result
            : throw new ArgumentException(
                "Not an address Charter can deliver to. Expected one address, for example person@example.com.",
                nameof(address));

    /// <summary>The RFC 5322 mailbox form, with the display name quoted when there is one.</summary>
    public string ToMailbox()
    {
        if (DisplayName is null)
        {
            return Address;
        }

        var escaped = DisplayName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"\"{escaped}\" <{Address}>";
    }

    /// <inheritdoc />
    public override string ToString() => Address;

    /// <summary>
    /// Strips anything that could end a header line early, and collapses the rest.
    /// </summary>
    private static string? CleanDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var builder = new StringBuilder(displayName.Length);
        foreach (var character in displayName)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// A rendered template: one subject, two bodies.
/// </summary>
/// <remarks>
/// Change spec 001 C.3 requires both renderings on every template, so they are one type rather than
/// two optional fields. A message with an HTML body and no plain text is not a valid message here,
/// and the compiler is a better enforcement of that than a review comment.
/// </remarks>
public sealed record EmailContent
{
    /// <summary>The subject line. Single line; encoded on the way out if it is not ASCII.</summary>
    public required string Subject { get; init; }

    /// <summary>The <c>text/plain</c> body. The one every client can render.</summary>
    public required string Text { get; init; }

    /// <summary>The <c>text/html</c> body.</summary>
    public required string Html { get; init; }
}

/// <summary>An addressed, rendered message, ready for a provider.</summary>
public sealed record EmailMessage
{
    /// <summary>Who it goes to. One recipient per message: no shared To, no accidental disclosure.</summary>
    public required EmailAddress To { get; init; }

    /// <summary>The rendered subject and both bodies.</summary>
    public required EmailContent Content { get; init; }

    /// <summary>Which rate-limit bucket this message is counted against.</summary>
    public required EmailCategory Category { get; init; }

    /// <summary>
    /// A short, non-identifying label for logs and the admin delivery list - <c>invitation</c>,
    /// <c>password_reset</c>, <c>needs_input</c>, <c>preview_ready</c>, <c>test</c>.
    /// </summary>
    public required string Kind { get; init; }
}
