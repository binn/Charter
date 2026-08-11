using Charter.Domain;
using Charter.VersionControl;

namespace Charter.Auth.Authorization;

/// <summary>
/// Section 26.10's three gates on creating a repository, evaluated together.
/// </summary>
/// <remarks>
/// <para>
/// Repo creation is a privilege escalation: a new repository is one nobody has scoped (section 7.3),
/// no CODEOWNERS file protects, and no branch protection covers, and it is created by the same
/// installation token every session runs under. Section 26.10 therefore gates it three ways -
/// instance opt-in, provider scope, and a capability distinct from any role - and all three must
/// hold.
/// </para>
/// <para>
/// The order the reasons are checked in is the order section 26.10 lists them, and it is the order
/// an operator can act on: <c>CHARTER_ALLOW_REPO_CREATION</c> is a variable they own outright, the
/// provider scope is a permission they grant deliberately, and the capability is a row an admin
/// edits. Answering with the outermost refusal first means the message names the thing furthest from
/// the user, which is the thing that has to change first.
/// </para>
/// </remarks>
public static class RepoCreationPolicy
{
    /// <summary>May this member create a repository on this instance?</summary>
    /// <param name="member">The acting member.</param>
    /// <param name="instanceAllows">
    /// <c>CHARTER_ALLOW_REPO_CREATION</c>. Gate 1, and false by default.
    /// </param>
    /// <param name="provider">
    /// What the version control provider can actually do. Gate 2 is
    /// <see cref="VersionControlCapabilities.RepoCreation"/>, which for a GitHub App is a permission
    /// the operator granted rather than something Charter can arrange for itself.
    /// </param>
    public static AuthorizationDecision CanCreateRepo(
        MemberSnapshot member,
        bool instanceAllows,
        VersionControlCapabilities provider)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(provider);

        if (!instanceAllows)
        {
            return AuthorizationDecision.Deny(
                "this instance does not create repositories: set CHARTER_ALLOW_REPO_CREATION=true "
                + "to allow it (section 26.10)");
        }

        if (!provider.RepoCreation)
        {
            return AuthorizationDecision.Deny(
                "the connected code host cannot create repositories for this instance: grant the "
                + "app organisation-level repository creation and reconnect (section 26.10)");
        }

        if (!member.HasCapability(MemberCapability.CanCreateRepo))
        {
            return AuthorizationDecision.Deny(
                "creating a repository needs the can_create_repo capability, which an admin grants");
        }

        // Notable: section 7.3's audit log wants every escalation attributable to a named human, and
        // this is the largest one Charter has.
        return AuthorizationDecision.Allow(
            "the instance allows repository creation, the provider supports it, and the member holds "
            + "can_create_repo",
            AuditActions.RepoCreationAuthorized);
    }
}
