namespace Charter.Domain;

/// <summary>The pull request states Charter observes. It never sets them: it has no merge button.</summary>
public enum PullRequestState
{
    Draft,

    Open,

    Closed,

    Merged,
}

/// <summary>
/// The pull request a session opened (section 5). Charter observes it and never merges it — merge
/// authority lives in GitHub branch protection and CODEOWNERS, outside Charter's trust boundary
/// (section 7.4).
/// </summary>
public sealed class PullRequest
{
    private PullRequest()
    {
    }

    private PullRequest(
        Guid id,
        Guid sessionId,
        int number,
        string url,
        string headSha,
        PullRequestState state,
        DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Number = number;
        Url = url;
        HeadSha = headSha;
        State = state;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public int Number { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public string HeadSha { get; private set; } = string.Empty;

    public PullRequestState State { get; private set; }

    /// <summary>
    /// Section 17: set when the base branch moved ahead <em>and</em> the changed files overlap.
    /// Being merely behind is not stale.
    /// </summary>
    public bool IsStale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PullRequest Open(
        Guid sessionId,
        int number,
        string url,
        string headSha,
        PullRequestState state = PullRequestState.Open,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        return new PullRequest(
            id ?? Guid.CreateVersion7(),
            sessionId,
            number,
            url.Trim(),
            headSha.Trim(),
            state,
            DomainTime.Resolve(now));
    }

    public void UpdateState(PullRequestState state, string? headSha = null, DateTimeOffset? now = null)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(headSha))
        {
            HeadSha = headSha.Trim();
            IsStale = false;
        }

        UpdatedAt = DomainTime.Resolve(now);
    }

    public void MarkStale(DateTimeOffset? now = null)
    {
        IsStale = true;
        UpdatedAt = DomainTime.Resolve(now);
    }
}
