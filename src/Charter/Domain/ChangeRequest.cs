namespace Charter.Domain;

/// <summary>The change request states Charter observes. It never sets them: it has no merge button.</summary>
/// <remarks>
/// Named for the provider-neutral concept of change spec 001 part A.2 rather than for GitHub's
/// spelling of it. What a user is shown is the provider's own word — <em>pull request</em> on GitHub
/// and Gitea, <em>merge request</em> on GitLab, <em>changelist</em> on Perforce — which arrives from
/// <c>IVersionControlProvider.Terms</c> and never from this enum.
/// </remarks>
public enum ChangeRequestState
{
    Draft,

    Open,

    Closed,

    Merged,
}

/// <summary>
/// The change request a session opened (section 5, as amended by change spec 001 part A.2). Charter
/// observes it and never merges it — merge authority lives in provider-side branch protection and
/// CODEOWNERS, outside Charter's trust boundary (section 7.4).
/// </summary>
/// <remarks>
/// A change request is deliberately <em>not</em> modelled as a branch. Part A.7 records why: a
/// Perforce shelved changelist is a change request with no branch behind it, so
/// <see cref="HeadBranch"/> is a fact some providers report and others do not, rather than the
/// identity of the row.
/// </remarks>
public sealed class ChangeRequest
{
    private ChangeRequest()
    {
    }

    private ChangeRequest(
        Guid id,
        Guid sessionId,
        int number,
        string url,
        string headSha,
        string? headBranch,
        ChangeRequestState state,
        DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Number = number;
        Url = url;
        HeadSha = headSha;
        HeadBranch = headBranch;
        State = state;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public int Number { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public string HeadSha { get; private set; } = string.Empty;

    /// <summary>
    /// The branch the session pushed to, as section 27.7's engineer <c>Details</c> disclosure names
    /// it alongside the change request number and the commit SHA.
    /// </summary>
    /// <remarks>
    /// Nullable rather than empty-defaulted: a change request row can be created from a webhook that
    /// carries no ref — and on a provider with no branches there is none to carry — so <c>null</c>
    /// says "not recorded" where <c>""</c> would read as a branch with no name. Nothing invents one
    /// at read time.
    /// </remarks>
    public string? HeadBranch { get; private set; }

    public ChangeRequestState State { get; private set; }

    /// <summary>
    /// Section 17: set when the base branch moved ahead <em>and</em> the changed files overlap.
    /// Being merely behind is not stale.
    /// </summary>
    public bool IsStale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ChangeRequest Open(
        Guid sessionId,
        int number,
        string url,
        string headSha,
        ChangeRequestState state = ChangeRequestState.Open,
        DateTimeOffset? now = null,
        Guid? id = null,
        string? headBranch = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        return new ChangeRequest(
            id ?? Guid.CreateVersion7(),
            sessionId,
            number,
            url.Trim(),
            headSha.Trim(),
            Clean(headBranch),
            state,
            DomainTime.Resolve(now));
    }

    public void UpdateState(
        ChangeRequestState state,
        string? headSha = null,
        DateTimeOffset? now = null,
        string? headBranch = null)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(headSha))
        {
            HeadSha = headSha.Trim();
            IsStale = false;
        }

        // A ref that arrives later fills the gap; one that does not arrive leaves what was recorded
        // before rather than blanking it.
        HeadBranch = Clean(headBranch) ?? HeadBranch;

        UpdatedAt = DomainTime.Resolve(now);
    }

    public void MarkStale(DateTimeOffset? now = null)
    {
        IsStale = true;
        UpdatedAt = DomainTime.Resolve(now);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
