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
        string? authorLogin,
        ChangeRequestState state,
        DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Number = number;
        Url = url;
        HeadSha = headSha;
        HeadBranch = headBranch;
        AuthorLogin = authorLogin;
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

    /// <summary>
    /// Who the provider records as having opened it, in the provider's own namespace — a GitHub
    /// login, usually Charter's own App installation identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists for one failure, and it is the one section 18 warns about: Railway will not deploy
    /// a change request branch from an account outside the workspace unless it has been invited with
    /// that account. Nothing reports that as an error. The preview simply never arrives, and Charter
    /// can only detect it by absence — so the one thing that turns the warning into a sentence
    /// somebody can act on is the account's name. Without this column the warning could only say
    /// "the author", which tells an operator to go and invite somebody they now have to work out the
    /// identity of.
    /// </para>
    /// <para>
    /// Nullable because a provider need not report it and a row created before this column existed
    /// has none. <c>null</c> means "not recorded", and the warning falls back to "the author" rather
    /// than naming somebody it is guessing at.
    /// </para>
    /// </remarks>
    public string? AuthorLogin { get; private set; }

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
        string? headBranch = null,
        string? authorLogin = null)
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
            Clean(authorLogin),
            state,
            DomainTime.Resolve(now));
    }

    public void UpdateState(
        ChangeRequestState state,
        string? headSha = null,
        DateTimeOffset? now = null,
        string? headBranch = null,
        string? authorLogin = null)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(headSha))
        {
            HeadSha = headSha.Trim();
            IsStale = false;
        }

        // A ref that arrives later fills the gap; one that does not arrive leaves what was recorded
        // before rather than blanking it. The author is treated the same way, so a row opened before
        // the provider reported one can still gain a name later.
        HeadBranch = Clean(headBranch) ?? HeadBranch;
        AuthorLogin = Clean(authorLogin) ?? AuthorLogin;

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
