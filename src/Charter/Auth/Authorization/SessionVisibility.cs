using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>
/// What one member may be shown of one session — computed on the server, so the API can omit fields
/// rather than send them and hope the client hides them.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.4. Panes 2 and 3 render only if that user has repository read access. A requester
/// toggling views is a display preference; it is not an input to this type and there is nowhere to
/// pass one. Transcripts leak file paths, environment variable names and error output, so the
/// question "did the user ask to see it" never reaches the decision.
/// </para>
/// <para>
/// Every field is a separate flag rather than a single "level", because the API projects a response
/// field-by-field. A serialiser that writes <c>Transcript == false</c> as an absent property is the
/// enforcement; CSS is not.
/// </para>
/// </remarks>
public sealed record SessionVisibility
{
    /// <summary>The requester-facing status thread of section 11.</summary>
    public required bool StatusThread { get; init; }

    /// <summary>The promoted, requester-facing subset of the event stream (section 5).</summary>
    public required bool Milestones { get; init; }

    /// <summary>Pane 2. Repository read access only (section 7.4).</summary>
    public required bool Transcript { get; init; }

    /// <summary>Pane 3. Repository read access only (section 7.4).</summary>
    public required bool Code { get; init; }

    /// <summary>Repository name, branch, commit. Section 7.1: a requester never sees any of it.</summary>
    public required bool RepositoryIdentity { get; init; }

    /// <summary>Cost and token counts. Section 7.1: never shown to a requester.</summary>
    public required bool Cost { get; init; }

    /// <summary>Nothing at all — what a member of another organisation gets.</summary>
    public static SessionVisibility None { get; } = new()
    {
        StatusThread = false,
        Milestones = false,
        Transcript = false,
        Code = false,
        RepositoryIdentity = false,
        Cost = false,
    };

    /// <summary>True when the member may see nothing of this session and the API should 404 it.</summary>
    public bool IsEmpty => !StatusThread && !Milestones && !Transcript && !Code && !RepositoryIdentity && !Cost;
}

/// <summary>Applies section 7.4 to one member, one repository and one session.</summary>
public static class SessionVisibilityPolicy
{
    /// <summary>
    /// Resolves what may be sent. Pure: the same inputs always give the same answer, and no input
    /// carries a user preference or an organisation mode.
    /// </summary>
    public static SessionVisibility For(MemberSnapshot member, RepoSnapshot repo, SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(session);

        if (member.OrgId != session.OrgId || repo.RepoId != session.RepoId || repo.OrgId != session.OrgId)
        {
            return SessionVisibility.None;
        }

        // The one gate section 7.4 names. Everything below reads it; nothing below recomputes it.
        var repoRead = RepoAccessPolicy.CanReadRepository(member, repo).IsAllowed;

        var isRequester = member.UserId == session.RequesterUserId;
        var approver = member.HasRole(MemberRole.Approver);
        var admin = member.HasRole(MemberRole.Admin);

        var followsThread = isRequester || repoRead || approver;

        return new SessionVisibility
        {
            StatusThread = followsThread,
            Milestones = followsThread,
            Transcript = repoRead,
            Code = repoRead,
            RepositoryIdentity = repoRead || admin,
            Cost = repoRead || approver || admin,
        };
    }

    /// <summary>Pane 2, as a decision with a reason the API can return.</summary>
    public static AuthorizationDecision CanViewTranscript(
        MemberSnapshot member,
        RepoSnapshot repo,
        SessionSnapshot session)
        => For(member, repo, session).Transcript
            ? AuthorizationDecision.Allow("the member has read access to the repository")
            : AuthorizationDecision.Deny("viewing a transcript needs read access to the repository");

    /// <summary>Pane 3, as a decision with a reason the API can return.</summary>
    public static AuthorizationDecision CanViewCode(
        MemberSnapshot member,
        RepoSnapshot repo,
        SessionSnapshot session)
        => For(member, repo, session).Code
            ? AuthorizationDecision.Allow("the member has read access to the repository")
            : AuthorizationDecision.Deny("viewing the code pane needs read access to the repository");
}
