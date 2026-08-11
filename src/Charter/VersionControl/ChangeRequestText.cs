using System.Text;
using Charter.Notifications;

namespace Charter.VersionControl;

/// <summary>
/// Renders execution-plane text inert before it is spliced into a document Charter signs (section 16.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The change request body is Charter's own account of the session, and an
/// engineer reads it as one. The check names and summaries in it come from <c>check_result</c> events, which
/// the shim posts from the same process as an agent reading untrusted repository content — so a
/// <c>summary</c> is attacker-influenced input. Spliced raw into markdown it becomes live content in
/// Charter's voice: a link to somewhere, a blockquote that fakes the disclosure of section 7.5, a heading
/// that hides the failing-check block underneath it, an <c>@mention</c> that pages a real person.
/// </para>
/// <para>
/// The requester side already holds this line — <see cref="RequesterSafeText"/> bounds and flattens free
/// text, and the requester's <c>Markdown.tsx</c> supports no link syntax at all. The change request body was
/// the one surface where plane-supplied text stayed clickable.
/// </para>
/// <para>
/// <strong>A code span rather than an escape table.</strong> Section 16.3 allows this text to be displayed —
/// it is the only account of what the checks did — so the treatment is presentation, not refusal. It is
/// rendered as an inline code span, which is inert by construction: nothing inside one is a link, an
/// autolink, a mention, an image, an entity or HTML, in GitHub-flavoured markdown or any other CommonMark
/// dialect. That is a stronger guarantee than escaping every punctuation mark that currently has a meaning,
/// because it does not have to be re-audited when a renderer grows a new one — and it is honest besides:
/// this is verbatim tool output, not prose Charter wrote.
/// </para>
/// </remarks>
public static class ChangeRequestText
{
    /// <summary>The most of one check's name the body will carry.</summary>
    public const int MaxCheckName = 100;

    /// <summary>The most of one check's summary the body will carry.</summary>
    public const int MaxCheckSummary = 300;

    /// <summary>
    /// The most checks the body will list.
    /// </summary>
    /// <remarks>
    /// A bound on the whole document, not on politeness. Nothing stops a session emitting ten thousand
    /// <c>check_result</c> events, and a body over GitHub's limit fails the create call — which would mean a
    /// session could stop its own work being reviewed by talking too much. The overflow is stated rather
    /// than dropped silently.
    /// </remarks>
    public const int MaxChecks = 20;

    /// <summary>
    /// One line of plane-supplied text as an inline code span, or empty when there is nothing to show.
    /// </summary>
    public static string CodeSpan(string? text, int maxLength)
    {
        var flattened = Flatten(text, maxLength);

        return flattened.Length == 0 ? string.Empty : $"`{flattened}`";
    }

    /// <summary>
    /// One line, bounded, with everything that could close a code span or start a new block removed.
    /// </summary>
    /// <remarks>
    /// Backticks go because they are the one character a code span cannot contain. Line breaks and control
    /// characters go because a single newline turns one list item into a block of Charter's own document —
    /// the same reason <see cref="RequesterSafeText.OwnWords"/> flattens them for an email. Truncation is
    /// <see cref="RequesterSafeText.Truncate"/> itself, so the two surfaces cut text the same way.
    /// </remarks>
    public static string Flatten(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (character == '`')
            {
                continue;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var single = string.Join(' ', builder.ToString().Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return single.Length == 0 ? string.Empty : RequesterSafeText.Truncate(single, maxLength);
    }
}
