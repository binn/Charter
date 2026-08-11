namespace Charter.Auth;

/// <summary>
/// The dotted verbs written to <c>audit_logs</c> by the authorisation and identity layer
/// (section 7.3, guardrail 5).
/// </summary>
/// <remarks>
/// Constants rather than free strings so a rename is a compile error and so a reader can see the
/// whole vocabulary in one place. Every one of these is attributable to a named human; Charter never
/// acts on its own initiative, so an entry from this list always has an actor.
/// </remarks>
public static class AuditActions
{
    /// <summary>The one-time setup token was redeemed and the first admin account created.</summary>
    public const string SetupCompleted = "setup.completed";

    /// <summary>A person signed in through one of the section 21 providers.</summary>
    public const string SignedIn = "auth.signed_in";

    /// <summary>A sign-in was refused. Recorded because a run of these is the interesting signal.</summary>
    public const string SignInFailed = "auth.sign_in_failed";

    /// <summary>An additional provider identity was attached to an existing user.</summary>
    public const string IdentityLinked = "auth.identity.linked";

    /// <summary>A person signed out. The other half of a session's story.</summary>
    public const string SignedOut = "auth.signed_out";

    /// <summary>A password was changed. The value is never recorded, only the fact.</summary>
    public const string PasswordChanged = "auth.password.changed";

    /// <summary>
    /// A one-time password-reset link was minted for somebody.
    /// </summary>
    /// <remarks>
    /// Written whether an administrator asked for it or the person did, because "who could have
    /// reset this account's password, and when" is the question this row exists to answer. It never
    /// carries the link.
    /// </remarks>
    public const string PasswordResetRequested = "auth.password.reset_requested";

    /// <summary>An admin invited somebody to the organisation (section 30.2).</summary>
    public const string MemberInvited = "member.invited";

    /// <summary>An admin withdrew an invitation nobody had spent.</summary>
    public const string MemberInviteRevoked = "member.invite.revoked";

    /// <summary>An invitation was redeemed and the account created (section 30.2).</summary>
    public const string MemberInviteAccepted = "member.invite.accepted";

    /// <summary>Somebody became able to file against a repository who previously could not.</summary>
    public const string RepoScopeGranted = "repo.scope.granted";

    /// <summary>A repository scope grant was withdrawn.</summary>
    public const string RepoScopeRevoked = "repo.scope.revoked";

    /// <summary>The spend gate of section 7.5 was passed by a named approver.</summary>
    public const string SpecApproved = "spec.approved";

    /// <summary>A specification reached <c>Queued</c> without a human approving it (section 7.5).</summary>
    public const string SessionAutoDispatched = "session.auto_dispatched";

    /// <summary>A role was added to a member. Privilege escalation is always audited.</summary>
    public const string MemberRoleGranted = "member.role.granted";

    /// <summary>A role was removed from a member.</summary>
    public const string MemberRoleRevoked = "member.role.revoked";
}
