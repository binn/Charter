using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Notifications;

/// <summary>
/// Strips the engineer-facing debris that must never reach a requester (sections 7.1, 11).
/// </summary>
/// <remarks>
/// <para>
/// Section 7.1 says a requester never sees a repo name, a branch, a diff or a token count. Section
/// 11 says failure has dignity, and that a non-engineer who sees a stack trace once never files
/// again. Email is the surface where that is hardest to hold, because the text reaching a template
/// has usually passed through a component that had every right to include a commit SHA.
/// </para>
/// <para>
/// The primary control is structural: the requester-facing template models in
/// <see cref="EmailTemplates"/> have no field for a repository, a branch or a commit. This is the
/// second layer, for the free text those models do carry - a question the agent asked, a line of
/// acceptance criteria - and it is deliberately conservative. It removes the shapes that are
/// unambiguous, and it does not attempt to paraphrase.
/// </para>
/// <para>
/// A bare <c>owner/repo</c> is <em>not</em> one of the shapes removed, and that is a decision rather
/// than an oversight. No regular expression separates <c>acme/checkout</c> from <c>and/or</c> or
/// <c>read/write</c>, and a scrubber that mangles a requester's own sentence to protect them from a
/// repository name has made the email worse in the common case to improve it in a case the model
/// already prevents. Commit hashes, forge URLs, <c>owner/repo@sha</c>, <c>.git</c> paths and stack
/// frames have no innocent reading, so those go.
/// </para>
/// </remarks>
public static partial class RequesterSafeText
{
    /// <summary>What a removed fragment is replaced with.</summary>
    public const string Placeholder = "…";

    /// <summary>Longest run of free text a template will render before truncating.</summary>
    public const int MaxLength = 600;

    /// <summary>Cleans <paramref name="text"/> for a requester-facing template.</summary>
    public static string Scrub(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || IsStackFrame(trimmed))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        var single = ForgeReference().Replace(builder.ToString(), Placeholder);
        single = CommitSha().Replace(single, Placeholder);
        single = Whitespace().Replace(single, " ").Trim();

        return Truncate(single, maxLength);
    }

    /// <summary>
    /// Normalises a requester's own words: whitespace and length only.
    /// </summary>
    /// <remarks>
    /// Their sentence is theirs. Scrubbing it would mean deleting parts of what somebody typed on
    /// the suspicion that a fragment of it looks like a commit hash, and quoting a request back
    /// wrongly is a worse failure than quoting it verbatim.
    /// </remarks>
    public static string OwnWords(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return Truncate(Whitespace().Replace(builder.ToString(), " ").Trim(), maxLength);
    }

    /// <summary>Truncates on a word boundary, with an ellipsis, so a sentence never stops mid-word.</summary>
    public static string Truncate(string text, int maxLength = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', maxLength - 1);
        var head = cut > maxLength / 2 ? text[..cut] : text[..(maxLength - 1)];

        return head.TrimEnd(' ', ',', ';', ':', '.') + Placeholder;
    }

    /// <summary>
    /// True for the lines a .NET or Node stack trace is made of.
    /// </summary>
    /// <remarks>
    /// Matching the frame rather than the exception message is deliberate. The first line of an
    /// exception is often the only part with any meaning, and a template that has been handed one is
    /// better off truncating it than emitting nothing at all - but the frames underneath carry file
    /// paths and internal type names and never help anybody who is not reading the code.
    /// </remarks>
    private static bool IsStackFrame(string line)
        => line.StartsWith("at ", StringComparison.Ordinal)
           || line.StartsWith("--- End of stack trace", StringComparison.Ordinal)
           || line.StartsWith("--- End of inner exception", StringComparison.Ordinal)
           || ExceptionHeader().IsMatch(line)
           || StackFrameWithLocation().IsMatch(line);

    /// <summary>
    /// A commit hash: twenty or more hex characters, or a shorter run that mixes letters and
    /// digits. The mixed requirement keeps ordinary words spelled out of <c>a</c> to <c>f</c>, and
    /// ordinary numbers, out of the match.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:[0-9a-f]{20,40}|(?=[0-9a-f]{7,19}\b)(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{7,19})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitSha();

    /// <summary>
    /// A reference that can only be to source control: a forge URL, an <c>owner/repo@sha</c>, or a
    /// path ending in <c>.git</c>.
    /// </summary>
    [GeneratedRegex(
        @"(?:https?://)?(?:[\w-]+\.)*(?:github|gitlab|bitbucket)\.(?:com|org|io)/[\w.-]+(?:/[\w.-]+)*" +
        @"|\b[\w.-]+/[\w.-]+@[0-9a-f]{7,40}\b" +
        @"|\b[\w.-]+/[\w.-]+\.git\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForgeReference();

    [GeneratedRegex(@"^\s*(?:at\s+)?\S+\.\S+\(.*\)(?:\s+in\s+.+:line\s+\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameWithLocation();

    /// <summary>
    /// The first line of a .NET exception: a dotted type name ending in <c>Exception</c>, then the
    /// message. Dropped whole - the type name is meaningless to the reader and the message is
    /// usually written for whoever is holding the code.
    /// </summary>
    [GeneratedRegex(@"^[\w.`+]*Exception\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionHeader();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
