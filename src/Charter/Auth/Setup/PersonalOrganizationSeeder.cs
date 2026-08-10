using Charter.Configuration;
using Charter.Domain;

namespace Charter.Auth.Setup;

/// <summary>The rows a brand new instance starts with.</summary>
/// <param name="Organization">The tenant. Personal or organisation, same table.</param>
/// <param name="User">The human.</param>
/// <param name="Member">Their membership, carrying every role (section 7.2).</param>
/// <param name="Identity">Their password identity, carrying the hash and never the password.</param>
/// <param name="AutoDispatch">The organisation-default spend policy (section 7.5).</param>
public sealed record SeededInstance(
    Organization Organization,
    User User,
    Member Member,
    Domain.Identity Identity,
    AutoDispatchPolicy AutoDispatch);

/// <summary>
/// Seeds the first organisation, member and policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Section 7.2 lives here and nowhere else.</b> Personal mode is an <see cref="Organization"/>
/// with one <see cref="Member"/> holding all roles and approval gates auto-satisfied by policy. That
/// is expressed entirely as seeded rows: <see cref="Member.AllRoles"/>, and an organisation-default
/// <see cref="AutoDispatchPolicy"/> with <c>enabled = true</c>. Nothing downstream reads
/// <see cref="Organization.Mode"/> to decide anything — the authorisation namespace cannot even see
/// it, because no snapshot carries it.
/// </para>
/// <para>
/// Inviting a second user is therefore the only thing that changes, and it needs no migration: add a
/// <see cref="Member"/> row with fewer roles, and, if the organisation wants approvals, flip the
/// seeded policy's <c>enabled</c> to false. The same resolver reads it either way.
/// </para>
/// </remarks>
public static class PersonalOrganizationSeeder
{
    /// <summary>The organisation name used when the operator does not supply one.</summary>
    public const string DefaultOrganizationName = "My workspace";

    /// <summary>
    /// Builds the rows for a first admin. The caller supplies an already-hashed verifier; this never
    /// sees a password.
    /// </summary>
    /// <param name="mode">
    /// From <c>CHARTER_MODE</c>. This is the one place it is read. It picks the <em>seeded defaults</em>
    /// — all it changes is whether the organisation-default auto-dispatch policy starts enabled —
    /// and it is stored on the organisation as a label for the UI, not as an authorisation input.
    /// </param>
    public static SeededInstance Seed(
        string organizationName,
        string email,
        string displayName,
        string passwordHash,
        CharterMode mode = CharterMode.Personal,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var name = string.IsNullOrWhiteSpace(organizationName) ? DefaultOrganizationName : organizationName.Trim();

        var organization = Organization.Create(
            name,
            mode == CharterMode.Personal ? OrganizationMode.Personal : OrganizationMode.Organization,
            now);

        var user = User.Create(email, displayName, now: now);

        // Section 7.2: one member holding every role. Not a bypass - the roles are real rows, and
        // every check below reads them the same way it reads a twenty-person organisation's.
        var member = Member.Create(
            organization.Id,
            user.Id,
            Member.AllRoles,
            // Section 26.10: repo creation is a privilege escalation and is granted deliberately.
            // The first admin of an instance is exactly who it is granted to.
            [MemberCapability.CanCreateRepo],
            now);

        var identity = Providers.PasswordIdentityProvider.NewPasswordIdentity(user.Id, passwordHash, now);

        // Section 7.5: "Personal mode - everything auto-dispatches. There is nobody to approve."
        // Expressed as the organisation-default row, which is the same row an organisation writes.
        var autoDispatch = AutoDispatchPolicy.Create(
            organization.Id,
            enabled: mode == CharterMode.Personal,
            now: now);

        return new SeededInstance(organization, user, member, identity, autoDispatch);
    }
}
