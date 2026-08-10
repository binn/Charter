using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>
/// The spend gate of section 7.5, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Approving a specification says the work is worth burning tokens and quota on. It says nothing
/// about whether the resulting code may ship: the merge gate lives in GitHub branch protection and
/// CODEOWNERS, outside Charter's trust boundary, and is not represented in Charter's data model at
/// all. Conflating the two is the design error section 7.5 opens by naming.
/// </para>
/// <para>
/// Self-approval is permitted. In a one-member organisation the person who filed the request is also
/// the only approver, and forbidding it would mean a personal instance needing a special case —
/// exactly the branch section 7.2 exists to prevent. Because the merge gate cannot move, the worst
/// case of a loose spend gate is wasted tokens and a pull request nobody wanted.
/// </para>
/// </remarks>
public static class SpecApprovalPolicy
{
    public static AuthorizationDecision CanApprove(MemberSnapshot member, RepoSnapshot repo, SpecSnapshot spec)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(spec);

        if (member.OrgId != spec.OrgId || repo.RepoId != spec.RepoId || repo.OrgId != spec.OrgId)
        {
            return AuthorizationDecision.Deny("that specification belongs to a different organisation");
        }

        if (spec.IsApproved)
        {
            return AuthorizationDecision.Deny("that specification has already been approved");
        }

        if (repo.Status == RepoStatus.Disabled)
        {
            return AuthorizationDecision.Deny("that repository is disabled");
        }

        // Section 7.1: the approver gates spend. An admin who should also approve holds the approver
        // role as well - roles are additive, and the single member of a personal instance holds both.
        return member.HasRole(MemberRole.Approver)
            ? AuthorizationDecision.Allow("the member holds the approver role", AuditActions.SpecApproved)
            : AuthorizationDecision.Deny("approving a specification needs the approver role");
    }
}
