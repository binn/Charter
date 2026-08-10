using Charter.Auth.Audit;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Auth.Providers;

/// <summary>Which Charter user, if any, a verified external subject turned out to be.</summary>
public abstract record IdentityResolution
{
    private IdentityResolution()
    {
    }

    /// <summary>The subject was already linked to this user.</summary>
    public sealed record Linked(User User) : IdentityResolution;

    /// <summary>The subject's email matched an existing user, and a new identity row was written.</summary>
    public sealed record NewlyLinked(User User) : IdentityResolution;

    /// <summary>
    /// Nobody. Charter does not create accounts from a successful OAuth sign-in — that would be open
    /// registration through the side door, which is the hazard section 30.1 exists to close.
    /// </summary>
    public sealed record NoAccount(string Message) : IdentityResolution;
}

/// <summary>Turns a verified external subject into a Charter user, or refuses.</summary>
public interface IIdentityLinker
{
    Task<IdentityResolution> ResolveAsync(
        ExternalIdentity external,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IIdentityLinker" />
/// <remarks>
/// <para>
/// Resolution is by provider subject first and by email only as a fallback, because the subject is
/// what the provider guarantees is stable and the email is what a person can change. Matching a
/// verified subject to an existing account by email is what lets someone who was invited by email
/// sign in with GitHub the first time.
/// </para>
/// <para>
/// It never creates a <see cref="User"/>. Accounts come from the section 30.1 setup token or from an
/// invitation, both of which are deliberate acts by a named human, and both of which are audited.
/// </para>
/// </remarks>
public sealed class IdentityLinker : IIdentityLinker
{
    private readonly CharterDbContext database;
    private readonly IAuditWriter audit;

    public IdentityLinker(CharterDbContext database, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(audit);

        this.database = database;
        this.audit = audit;
    }

    /// <inheritdoc />
    public async Task<IdentityResolution> ResolveAsync(
        ExternalIdentity external,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(external);

        var linked = await (from identity in database.Identities
                            join user in database.Users on identity.UserId equals user.Id
                            where identity.Provider == external.Provider
                                  && identity.ProviderUserId == external.ProviderUserId
                            select user)
            .SingleOrDefaultAsync(cancellationToken);

        if (linked is not null)
        {
            return new IdentityResolution.Linked(linked);
        }

        if (string.IsNullOrWhiteSpace(external.Email))
        {
            return new IdentityResolution.NoAccount(
                "That account is not linked to anyone here, and the provider gave us no email address to match.");
        }

        var email = external.Email.Trim().ToLowerInvariant();
        var candidate = await database.Users.SingleOrDefaultAsync(row => row.Email == email, cancellationToken);

        if (candidate is null)
        {
            return new IdentityResolution.NoAccount(
                "There is no Charter account for that address. Ask an admin to invite you.");
        }

        database.Identities.Add(Identity.Create(candidate.Id, external.Provider, external.ProviderUserId));
        await database.SaveChangesAsync(cancellationToken);

        // Attaching a new way to sign in as an existing person is a privilege change, so it is
        // attributable (section 7.3). The org is whichever this user belongs to; a user with no
        // membership yet leaves no entry, because audit_logs is scoped to an organisation.
        var orgId = await database.Members
            .Where(row => row.UserId == candidate.Id)
            .OrderBy(row => row.CreatedAt)
            .Select(row => (Guid?)row.OrgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (orgId is { } org)
        {
            await audit.RecordAsync(
                new AuditEntry
                {
                    OrgId = org,
                    ActorUserId = candidate.Id,
                    Action = AuditActions.IdentityLinked,
                    TargetType = "user",
                    TargetId = candidate.Id.ToString(),
                    Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["provider"] = external.Provider.ToString().ToLowerInvariant(),
                    },
                },
                cancellationToken);
        }

        return new IdentityResolution.NewlyLinked(candidate);
    }
}
