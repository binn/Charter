using System.Text.Json;

namespace Charter.GitHub;

/// <summary>The webhook events Charter subscribes to. Everything else is acknowledged and ignored.</summary>
public enum GitHubWebhookEventType
{
    /// <summary>Anything Charter does not handle. Acknowledged so GitHub stops retrying.</summary>
    Unknown,

    /// <summary>GitHub's connectivity check, sent when the webhook is first configured.</summary>
    Ping,

    /// <summary>The App was installed, suspended, or removed.</summary>
    Installation,

    /// <summary>Repositories were added to or removed from an existing installation.</summary>
    InstallationRepositories,

    /// <summary>Commits landed. Section 17 cares; so does re-reading <c>.charter/</c> on the base branch.</summary>
    Push,

    /// <summary>A pull request opened, closed, merged, or moved.</summary>
    PullRequest,

    /// <summary>Checks finished for a commit — the "checks pass" leg of the smoke test (section 9).</summary>
    CheckSuite,

    /// <summary>
    /// Somebody commented on an issue or a pull request. Section 18's fallback preview path: the
    /// hosting provider's bot announces the environment in a comment, and on most platforms that is
    /// the only announcement there is.
    /// </summary>
    IssueComment,
}

/// <summary>
/// One verified webhook delivery, flattened to the handful of facts Charter acts on.
/// </summary>
/// <remarks>
/// <para>
/// Parsing to a narrow record rather than passing the raw JSON onwards is deliberate. A webhook body
/// is attacker-influenced input in the section 16 sense — anybody who can open a pull request can put
/// text in one — so the surface that reaches Charter's own logic is kept to identifiers, states and
/// SHAs. Nothing here carries a title or a branch description.
/// </para>
/// <para>
/// <see cref="CommentBody"/> is the one exception, and it is carried because section 18's fallback
/// ingestion path cannot exist without it: the provider's bot announces the preview in a comment and
/// nowhere else. It is treated as exactly what it is — untrusted text — and goes straight to
/// <c>PreviewCommentParser</c>, which extracts a URL and a state word from it and reads nothing else.
/// It is truncated on the way in so an unbounded attacker-supplied string never reaches a regular
/// expression, and it is never logged, never rendered, and never shown to a requester.
/// </para>
/// <para>
/// Every property is nullable because every one of them is absent from some event GitHub sends.
/// Parsing never throws on a shape it did not expect; an event Charter cannot read becomes
/// <see cref="GitHubWebhookEventType.Unknown"/> and is acknowledged.
/// </para>
/// </remarks>
public sealed record GitHubWebhookDelivery
{
    /// <summary>
    /// The most comment text carried, in characters. <c>PreviewCommentParser</c> bounds what it
    /// reads for the same reason; this bounds what is retained in the first place.
    /// </summary>
    public const int MaxCommentBodyLength = 16 * 1024;

    /// <summary>Which of the handled events this is.</summary>
    public required GitHubWebhookEventType Type { get; init; }

    /// <summary>The raw <c>X-GitHub-Event</c> value, for logs.</summary>
    public required string EventName { get; init; }

    /// <summary>The <c>X-GitHub-Delivery</c> GUID.</summary>
    public string? DeliveryId { get; init; }

    /// <summary>The event's <c>action</c>, where it has one.</summary>
    public string? Action { get; init; }

    /// <summary>The installation this delivery belongs to.</summary>
    public long? InstallationId { get; init; }

    /// <summary>The repository, as <c>owner/name</c>, for single-repository events.</summary>
    public string? RepositoryFullName { get; init; }

    /// <summary>
    /// Every repository named by an installation event — <c>repositories</c>,
    /// <c>repositories_added</c> or <c>repositories_removed</c> depending on the action.
    /// </summary>
    public IReadOnlyList<string> RepositoryFullNames { get; init; } = [];

    /// <summary>The repository's default branch, when the event reported one.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>The full ref a push landed on, such as <c>refs/heads/main</c>.</summary>
    public string? Ref { get; init; }

    /// <summary>The branch name a push landed on, with <c>refs/heads/</c> stripped.</summary>
    public string? Branch =>
        Ref is not null && Ref.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? Ref["refs/heads/".Length..]
            : null;

    /// <summary>The head commit — a push's <c>after</c>, a PR's head SHA, a check suite's head SHA.</summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// A push's <c>before</c>: where the branch pointed beforehand.
    /// </summary>
    /// <remarks>
    /// Section 17 needs it. Staleness is "behind <em>and</em> overlapping on changed files", and
    /// without the previous head there is no way to ask which files the push actually landed —
    /// only how far behind something is, which is the half that produces false positives.
    /// </remarks>
    public string? BeforeSha { get; init; }

    /// <summary>The pull request number, on a <c>pull_request</c> event.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>The pull request's head branch.</summary>
    public string? PullRequestHeadBranch { get; init; }

    /// <summary>The pull request's base branch.</summary>
    public string? PullRequestBaseBranch { get; init; }

    /// <summary>Whether a closed pull request was merged rather than abandoned.</summary>
    public bool? PullRequestMerged { get; init; }

    /// <summary>
    /// The issue or pull request number an <c>issue_comment</c> event was written on.
    /// </summary>
    /// <remarks>
    /// GitHub delivers pull request comments as <c>issue_comment</c> with the pull request in the
    /// <c>issue</c> object, so this is the change request number for anything Charter cares about.
    /// The comment path looks the number up against change requests this instance opened, and a
    /// number that matches nothing is ignored.
    /// </remarks>
    public int? IssueNumber { get; init; }

    /// <summary>Who wrote the comment — <c>railway[bot]</c>, <c>render[bot]</c>, a person.</summary>
    /// <remarks>
    /// The provider checks this before believing anything in the body, which is what stops a person
    /// pasting a link into a comment from redirecting a requester's preview.
    /// </remarks>
    public string? CommentAuthorLogin { get; init; }

    /// <summary>The comment text, truncated. Untrusted, and only ever read by the parser.</summary>
    public string? CommentBody { get; init; }

    /// <summary>A check suite's <c>conclusion</c>: <c>success</c>, <c>failure</c>, and so on.</summary>
    public string? CheckSuiteConclusion { get; init; }

    /// <summary>Whether a check suite finished successfully.</summary>
    public bool CheckSuiteSucceeded =>
        string.Equals(CheckSuiteConclusion, "success", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a verified delivery body. Call this only after the signature has been checked.
    /// </summary>
    /// <param name="eventName">The <c>X-GitHub-Event</c> header.</param>
    /// <param name="json">The body, which the signature has already vouched for.</param>
    /// <param name="deliveryId">The <c>X-GitHub-Delivery</c> header.</param>
    public static GitHubWebhookDelivery Parse(string eventName, string json, string? deliveryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(json);

        var type = eventName switch
        {
            "ping" => GitHubWebhookEventType.Ping,
            "installation" => GitHubWebhookEventType.Installation,
            "installation_repositories" => GitHubWebhookEventType.InstallationRepositories,
            "push" => GitHubWebhookEventType.Push,
            "pull_request" => GitHubWebhookEventType.PullRequest,
            "check_suite" => GitHubWebhookEventType.CheckSuite,
            "issue_comment" => GitHubWebhookEventType.IssueComment,
            _ => GitHubWebhookEventType.Unknown,
        };

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // A signed body that is not JSON is GitHub's problem, not something to crash on.
            return new GitHubWebhookDelivery
            {
                Type = GitHubWebhookEventType.Unknown,
                EventName = eventName,
                DeliveryId = deliveryId,
            };
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new GitHubWebhookDelivery
                {
                    Type = GitHubWebhookEventType.Unknown,
                    EventName = eventName,
                    DeliveryId = deliveryId,
                };
            }

            var repository = Object(root, "repository");
            var pullRequest = Object(root, "pull_request");
            var checkSuite = Object(root, "check_suite");

            // GitHub delivers a comment on a pull request as `issue_comment`, with the pull request
            // in the `issue` object and a `pull_request` marker inside it. Charter reads the number
            // from either shape rather than insisting on one, because a provider bot that comments
            // through a different route still names the same change request.
            var issue = Object(root, "issue");
            var comment = Object(root, "comment");

            return new GitHubWebhookDelivery
            {
                Type = type,
                EventName = eventName,
                DeliveryId = deliveryId,
                Action = Text(root, "action"),
                InstallationId = Number(Object(root, "installation"), "id"),
                RepositoryFullName = Text(repository, "full_name"),
                RepositoryFullNames = ReadRepositoryList(root),
                DefaultBranch = Text(repository, "default_branch"),
                Ref = type == GitHubWebhookEventType.Push ? Text(root, "ref") : null,
                HeadSha = ReadHeadSha(type, root, pullRequest, checkSuite),
                BeforeSha = type == GitHubWebhookEventType.Push ? Text(root, "before") : null,
                PullRequestNumber = Number(pullRequest, "number") is { } number ? (int)number : null,
                PullRequestHeadBranch = Text(Object(pullRequest, "head"), "ref"),
                PullRequestBaseBranch = Text(Object(pullRequest, "base"), "ref"),
                PullRequestMerged = Bool(pullRequest, "merged"),
                IssueNumber = Number(issue, "number") is { } issueNumber ? (int)issueNumber : null,
                CommentAuthorLogin = Text(Object(comment, "user"), "login"),
                CommentBody = Truncate(Text(comment, "body")),
                CheckSuiteConclusion = Text(checkSuite, "conclusion"),
            };
        }
    }

    private static string? ReadHeadSha(
        GitHubWebhookEventType type,
        JsonElement root,
        JsonElement? pullRequest,
        JsonElement? checkSuite) => type switch
        {
            GitHubWebhookEventType.Push => Text(root, "after"),
            GitHubWebhookEventType.PullRequest => Text(Object(pullRequest, "head"), "sha"),
            GitHubWebhookEventType.CheckSuite => Text(checkSuite, "head_sha"),
            _ => null,
        };

    private static IReadOnlyList<string> ReadRepositoryList(JsonElement root)
    {
        var names = new List<string>();

        foreach (var key in (string[])["repositories", "repositories_added", "repositories_removed"])
        {
            if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var element in array.EnumerateArray())
            {
                if (Text(element, "full_name") is { Length: > 0 } fullName && !names.Contains(fullName))
                {
                    names.Add(fullName);
                }
            }
        }

        return names;
    }

    private static string? Truncate(string? value)
        => value is { Length: > MaxCommentBodyLength } ? value[..MaxCommentBodyLength] : value;

    private static JsonElement? Object(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? Text(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static long? Number(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool? Bool(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>
/// Something that reacts to a verified webhook delivery.
/// </summary>
/// <remarks>
/// A fan-out seam rather than a single handler: onboarding (section 9) wants installation and
/// check-suite events, concurrency (section 17) wants pushes, and the request lifecycle wants pull
/// request state. Registering several keeps those three from growing into one method that knows
/// about all of them.
/// <para>
/// A listener that throws is logged and does not stop the others, and never changes the response
/// GitHub sees: a delivery that was genuinely signed has been received, whatever Charter then made
/// of it, and answering 500 only earns a redelivery of something that will fail the same way.
/// </para>
/// </remarks>
public interface IGitHubWebhookListener
{
    /// <summary>Handles one verified delivery.</summary>
    Task OnDeliveryAsync(GitHubWebhookDelivery delivery, CancellationToken cancellationToken = default);
}
