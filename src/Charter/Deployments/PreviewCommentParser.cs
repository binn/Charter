using System.Text.RegularExpressions;
using Charter.Domain;

namespace Charter.Deployments;

/// <summary>
/// The fragile but universal fallback of section 18: reading a preview out of a change request
/// comment, because Railway's bot comments when the environment is ready.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every failure here is "not yet".</strong> A comment is the wrong shape, the bot changed
/// its wording, somebody wrote a paragraph about the weather — none of those are errors, and treating
/// them as errors would turn the most common event on a change request into a stream of alerts. The
/// only thing this parser can do is recognise something; failing to recognise is the default state of
/// the world.
/// </para>
/// <para>
/// The body is attacker-influenced input in the section 16 sense: anybody who can comment on a change
/// request can write one. Nothing here trusts the text beyond extracting a URL and a state, callers
/// check the author, and the parsed result binds to a head commit that must already exist in this
/// instance — so the worst a convincing comment achieves is a wrong URL on a change request its author
/// could already comment on.
/// </para>
/// </remarks>
public static partial class PreviewCommentParser
{
    /// <summary>
    /// The most body text that is read. A comment longer than this has its tail ignored rather than
    /// handing an unbounded, attacker-supplied string to a regular expression.
    /// </summary>
    public const int MaxBodyLength = 16 * 1024;

    private static readonly (string Token, DeploymentState State)[] StateWords =
    [
        // Ordered by how badly Charter wants to know. A comment that says both "failed" and
        // "deployed" is a failure being reported next to a stale link, and reading it as ready would
        // put a dead URL in front of a requester.
        ("removed", DeploymentState.Expired),
        ("destroyed", DeploymentState.Expired),
        ("torn down", DeploymentState.Expired),
        ("deleted", DeploymentState.Expired),
        ("failed", DeploymentState.Failed),
        ("failure", DeploymentState.Failed),
        ("crashed", DeploymentState.Failed),
        ("error", DeploymentState.Failed),
        ("cancelled", DeploymentState.Cancelled),
        ("canceled", DeploymentState.Cancelled),
        ("skipped", DeploymentState.Cancelled),
        ("deploy successful", DeploymentState.Ready),
        ("deployment successful", DeploymentState.Ready),
        ("successfully deployed", DeploymentState.Ready),
        ("deployed", DeploymentState.Ready),
        ("is ready", DeploymentState.Ready),
        ("available at", DeploymentState.Ready),
        ("is live", DeploymentState.Ready),
        ("building", DeploymentState.Building),
        ("deploying", DeploymentState.Building),
        ("in progress", DeploymentState.Building),
        ("queued", DeploymentState.Pending),
        ("waiting", DeploymentState.Pending),
    ];

    /// <summary>True when the comment was written by one of <paramref name="logins"/>.</summary>
    /// <remarks>
    /// Case-insensitive, and tolerant of the <c>[bot]</c> suffix GitHub appends to App identities, so
    /// a caller can name <c>railway</c> and match <c>railway[bot]</c>.
    /// </remarks>
    public static bool IsFrom(DeploymentComment comment, IReadOnlyList<string> logins)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(logins);

        if (string.IsNullOrWhiteSpace(comment.AuthorLogin))
        {
            return false;
        }

        var author = Normalise(comment.AuthorLogin);

        foreach (var login in logins)
        {
            if (!string.IsNullOrWhiteSpace(login) && string.Equals(author, Normalise(login), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;

        static string Normalise(string login)
        {
            var trimmed = login.Trim().ToLowerInvariant();
            return trimmed.EndsWith("[bot]", StringComparison.Ordinal)
                ? trimmed[..^"[bot]".Length]
                : trimmed;
        }
    }

    /// <summary>Every absolute http(s) URL in the comment, in the order they appear.</summary>
    /// <remarks>
    /// Trailing punctuation and closing brackets are stripped, because a link inside markdown —
    /// <c>[preview](https://example.com)</c> — or at the end of a sentence otherwise arrives with a
    /// character that makes it a different URL.
    /// </remarks>
    public static IReadOnlyList<Uri> Urls(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var text = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
        var found = new List<Uri>();

        foreach (var match in UrlPattern().EnumerateMatches(text))
        {
            var candidate = text.Substring(match.Index, match.Length).TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'', '>', '`');

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var url)
                && url.Scheme is "http" or "https"
                && !found.Contains(url))
            {
                found.Add(url);
            }
        }

        return found;
    }

    /// <summary>The state the comment describes, or <see langword="null"/> when it describes none.</summary>
    public static DeploymentState? State(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var text = (body.Length > MaxBodyLength ? body[..MaxBodyLength] : body).ToLowerInvariant();

        foreach (var (token, state) in StateWords)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                return state;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one comment as a deployment report.
    /// </summary>
    /// <param name="comment">The comment. Never trusted beyond a URL and a state.</param>
    /// <param name="provider">The provider id to record the report under.</param>
    /// <param name="accept">
    /// Which URLs count as the preview. A platform's comment usually links its own dashboard as well
    /// as the deployment, and the dashboard is not something to put in front of a requester. Null
    /// accepts the first URL in the comment.
    /// </param>
    /// <returns>
    /// A report, or <see cref="DeploymentAvailability.NotYet"/> — which is what "this comment is about
    /// something else" looks like, and the answer for the overwhelming majority of comments.
    /// </returns>
    public static DeploymentObservation Read(
        DeploymentComment comment,
        string provider,
        Func<Uri, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var state = State(comment.Body);
        var url = Urls(comment.Body).FirstOrDefault(candidate => accept is null || accept(candidate));

        if (url is null)
        {
            // A failure reported without a link is still worth recording: the requester's card should
            // say the preview is not coming rather than spin on a skeleton forever.
            return state is DeploymentState.Failed or DeploymentState.Cancelled or DeploymentState.Expired
                ? DeploymentObservation.Reported(new DeploymentReport(
                    provider,
                    state.Value,
                    Detail: "read from a change request comment"))
                : DeploymentObservation.NotYet("no preview URL in this comment");
        }

        if (state is DeploymentState.Failed or DeploymentState.Cancelled or DeploymentState.Expired)
        {
            // A link next to a failure is a link to something that is not there.
            return DeploymentObservation.Reported(new DeploymentReport(
                provider,
                state.Value,
                Detail: "read from a change request comment"));
        }

        // A URL with no state word is the common shape of a bot comment that only ever appears once
        // the environment is up, so the link itself is the announcement.
        return DeploymentObservation.Reported(new DeploymentReport(
            provider,
            state ?? DeploymentState.Ready,
            url.ToString(),
            Detail: "read from a change request comment"));
    }

    [GeneratedRegex(@"https?://[^\s<>""'`\)\]]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlPattern();
}
