using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Asks <see cref="SessionVisibilityPolicy"/> what one member may be shown of one request.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.4 is expressed once, in <see cref="SessionVisibilityPolicy"/>, and this type does not
/// re-express it. It only supplies the policy's inputs: the policy is pure over an organisation, a
/// repository, and the identity of the person who filed the work, and a request carries all three
/// whether or not a session has started yet.
/// </para>
/// <para>
/// That is the whole reason a synthetic <see cref="SessionSnapshot"/> is built for a request in
/// <c>Refining</c>. The alternative — a second "request visibility" rule for the pre-session states —
/// is exactly the fork section 7.4 warns about: two rules that agree today and drift the first time
/// one of them is edited. Here there is one rule, and this file is an adapter to it.
/// </para>
/// </remarks>
public static class RequestVisibility
{
    /// <summary>
    /// What this member may be shown of this request, using the session's identity when one exists
    /// and the request's own identity before that.
    /// </summary>
    public static SessionVisibility Resolve(
        MemberSnapshot member,
        RepoSnapshot repo,
        Request request,
        Session? session)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(request);

        return SessionVisibilityPolicy.For(member, repo, Snapshot(request, session));
    }

    /// <summary>
    /// The snapshot the policy judges. A request without a session stands in for itself: same
    /// organisation, same repository, same requester — the three facts the policy actually reads.
    /// </summary>
    public static SessionSnapshot Snapshot(Request request, Session? session)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new SessionSnapshot
        {
            SessionId = session?.Id ?? request.Id,
            OrgId = request.OrgId,
            RepoId = request.RepoId,
            RequesterUserId = request.RequesterId,
        };
    }

    /// <summary>
    /// Whether the engineer <c>details</c> block may be sent (section 27.7).
    /// </summary>
    /// <remarks>
    /// The block bundles repository identity (pull request number, commit SHA, branch) with cost, so
    /// it needs both flags. Reading the two flags the policy already publishes — rather than asking a
    /// fresh question — is what keeps this file free of rules of its own.
    /// </remarks>
    public static bool CanSeeEngineerDetails(SessionVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        return visibility.RepositoryIdentity && visibility.Cost;
    }
}
