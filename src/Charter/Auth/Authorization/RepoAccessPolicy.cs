using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>
/// Guardrail 1 of section 7.3: who may file against which repository, and who may read one.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions over snapshots. No database, no <see cref="Organization"/>, no request context and
/// no user preference — the inputs make it impossible to write <c>if (personalMode) allow</c>
/// (section 7.2) or to let a client-side toggle decide anything (section 7.4).
/// </para>
/// <para>
/// Every method returns <see cref="AuthorizationDecision"/>, whose default value is a denial, so a
/// path that reaches the end of a method without finding a grant refuses.
/// </para>
/// </remarks>
public static class RepoAccessPolicy
{
    /// <summary>
    /// May this member file a request against this repository?
    /// </summary>
    /// <remarks>
    /// Deny by default. A newly connected repository has no <c>repo_scopes</c> rows and is therefore
    /// requestable by nobody; the only way to change that is to write a row, which
    /// <c>RepoScopeAdministration</c> does and audits.
    /// </remarks>
    public static AuthorizationDecision CanFileRequest(MemberSnapshot member, RepoSnapshot repo)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);

        if (member.OrgId != repo.OrgId)
        {
            return AuthorizationDecision.Deny("that repository belongs to a different organisation");
        }

        // Section 9: readiness is earned. A repo that has not passed its smoke test is invisible to
        // requesters no matter what scope rows exist, so this is checked before the grants.
        if (repo.Status != RepoStatus.Ready)
        {
            return AuthorizationDecision.Deny(
                "that repository is not ready for requests yet; its smoke test has not passed");
        }

        return ResolveScope(member, repo);
    }

    /// <summary>
    /// Section 7.4: repository read access — the single gate on the transcript pane and the code pane.
    /// </summary>
    /// <remarks>
    /// Section 7.1 gives repository-level detail — sessions, transcripts, diffs, steering — to the
    /// engineer, and only to the engineer. An admin who needs to read a transcript grants themselves
    /// the engineer role, which is a deliberate, audited act; section 7.5 is explicit that trust
    /// never escalates on its own, and silently folding admin into engineer would be exactly that.
    /// </remarks>
    public static AuthorizationDecision CanReadRepository(MemberSnapshot member, RepoSnapshot repo)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);

        if (member.OrgId != repo.OrgId)
        {
            return AuthorizationDecision.Deny("that repository belongs to a different organisation");
        }

        return member.HasRole(MemberRole.Engineer)
            ? AuthorizationDecision.Allow("the member holds the engineer role")
            : AuthorizationDecision.Deny("reading repository detail needs the engineer role");
    }

    /// <summary>
    /// Section 7.1: connecting repositories, configuring scope and reading the audit log are the
    /// admin's. Granting repository scope to somebody else is an administrative act, not an
    /// engineering one.
    /// </summary>
    public static AuthorizationDecision CanAdministerRepositoryScope(MemberSnapshot member, RepoSnapshot repo)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);

        if (member.OrgId != repo.OrgId)
        {
            return AuthorizationDecision.Deny("that repository belongs to a different organisation");
        }

        return member.HasRole(MemberRole.Admin)
            ? AuthorizationDecision.Allow("the member holds the admin role", AuditActions.RepoScopeGranted)
            : AuthorizationDecision.Deny("changing who may file against a repository needs the admin role");
    }

    /// <summary>
    /// Resolves the scope rows most-specific-first, and denies when none of them says yes.
    /// </summary>
    /// <remarks>
    /// A grant addressed to the member beats one addressed to a role, because it is the more specific
    /// statement about this person. Within a tier a withheld grant beats a given one: two role rows
    /// that disagree resolve to the stricter reading, which is the only safe way to read a conflict.
    /// </remarks>
    private static AuthorizationDecision ResolveScope(MemberSnapshot member, RepoSnapshot repo)
    {
        foreach (var grant in repo.Grants)
        {
            if (grant.MemberId == member.MemberId)
            {
                return grant.CanRequest
                    ? AuthorizationDecision.Allow("a repository scope grant names this member")
                    : AuthorizationDecision.Deny("a repository scope row withholds access from this member");
            }
        }

        var granted = false;

        foreach (var grant in repo.Grants)
        {
            if (grant.Role is not { } role || !member.HasRole(role))
            {
                continue;
            }

            if (!grant.CanRequest)
            {
                return AuthorizationDecision.Deny(
                    $"a repository scope row withholds access from the {DescribeRole(role)} role");
            }

            granted = true;
        }

        if (granted)
        {
            return AuthorizationDecision.Allow("a repository scope grant covers one of this member's roles");
        }

        // Section 7.3: deny by default. Falling through to the default value is the refusal, not an
        // oversight — there is no `return Allow` anywhere below a failed lookup.
        return AuthorizationDecision.Denied;
    }

    private static string DescribeRole(MemberRole role) => role switch
    {
        MemberRole.Requester => "requester",
        MemberRole.Approver => "approver",
        MemberRole.Engineer => "engineer",
        MemberRole.Admin => "admin",
        _ => role.ToString().ToLowerInvariant(),
    };
}
