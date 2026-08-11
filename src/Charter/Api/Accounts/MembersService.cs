using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Accounts;

/// <summary>
/// Members and roles (section 7.1, the administrator's column).
/// </summary>
/// <remarks>
/// <para>
/// <c>member.role.granted</c> and <c>member.role.revoked</c> existed in
/// <see cref="AuditActions"/> before anything wrote them. That is the shape of the bug this type
/// fixes: privilege escalation is the single most important thing an audit log records, and a verb
/// nobody writes records nothing. Every change here goes through
/// <see cref="SetRoleAsync"/>, and every call that changes something writes a row naming the
/// administrator who did it.
/// </para>
/// <para>
/// Section 7.2 applies without a branch. A personal instance is an organisation whose one member
/// holds every role, and it reaches this code on the same path as an organisation with forty — which
/// is why the last-administrator guard below is expressed as "this instance would have no
/// administrator" rather than as a personal-mode special case.
/// </para>
/// </remarks>
public sealed class MembersService
{
    private readonly CharterDbContext database;
    private readonly IAuditWriter audit;

    public MembersService(CharterDbContext database, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(audit);

        this.database = database;
        this.audit = audit;
    }

    /// <summary>Everybody in the organisation, with their roles. Administrators only.</summary>
    public async Task<(CommandOutcome Outcome, MembersResponse? Members)> ListAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (Refuse(), null);
        }

        var rows = await QueryAsync(member.OrgId, cancellationToken);

        return (
            CommandOutcome.Ok(),
            new MembersResponse
            {
                Members = [.. rows.Select(row => Describe(row.Member, row.DisplayName, row.Email, member))],
            });
    }

    /// <summary>
    /// Adds or removes one role, and audits it (section 7.3, guardrail 5).
    /// </summary>
    /// <remarks>
    /// Two things it refuses, both because the alternative is an instance nobody can administer:
    /// leaving a member with no role at all, and removing the last administrator. Neither is a
    /// permission check — an administrator is entitled to do both in principle — so both come back
    /// as a sentence explaining what would break rather than as "forbidden".
    /// </remarks>
    public async Task<(CommandOutcome Outcome, MemberResponse? Member)> SetRoleAsync(
        MemberSnapshot member,
        Guid targetMemberId,
        SetMemberRoleBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (Refuse(), null);
        }

        if (body.Role is not { } apiRole)
        {
            return (CommandOutcome.Invalid("Name the role to add or remove."), null);
        }

        var role = apiRole.ToDomain();

        var target = await database.Members
            .SingleOrDefaultAsync(row => row.Id == targetMemberId && row.OrgId == member.OrgId, cancellationToken);

        if (target is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var person = await database.Users
            .AsNoTracking()
            .Where(row => row.Id == target.UserId)
            .Select(row => new { row.DisplayName, row.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (person is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        // Nothing to do, and nothing to audit. An audit log full of no-ops is one nobody reads.
        if (target.HasRole(role) == body.Granted)
        {
            return (CommandOutcome.Ok(), Describe(target, person.DisplayName, person.Email, member));
        }

        if (!body.Granted)
        {
            if (target.Roles.Count == 1)
            {
                return (
                    CommandOutcome.Conflict(
                        $"{person.DisplayName} would be left with no role at all, which is not a state "
                        + "Charter can represent. Give them another role first, or remove them from "
                        + "the organisation."),
                    null);
            }

            if (role == MemberRole.Admin && await IsLastAdminAsync(member.OrgId, target.Id, cancellationToken))
            {
                return (
                    CommandOutcome.Conflict(
                        "That is the last administrator on this instance. Make somebody else an "
                        + "administrator first — otherwise nobody could connect a repository, change "
                        + "a budget or read this log again."),
                    null);
            }
        }

        if (body.Granted)
        {
            target.GrantRole(role);
        }
        else
        {
            target.RevokeRole(role);
        }

        await database.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = member.UserId,
                Action = body.Granted ? AuditActions.MemberRoleGranted : AuditActions.MemberRoleRevoked,
                TargetType = nameof(Member),
                TargetId = target.Id.ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["role"] = apiRole.ToString().ToLowerInvariant(),
                    ["member_email"] = person.Email,
                },
            },
            cancellationToken);

        return (CommandOutcome.Ok(), Describe(target, person.DisplayName, person.Email, member));
    }

    private async Task<IReadOnlyList<(Member Member, string DisplayName, string Email)>> QueryAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Members
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .Join(
                database.Users.AsNoTracking(),
                row => row.UserId,
                user => user.Id,
                (row, user) => new { Member = row, user.DisplayName, user.Email })
            .OrderBy(row => row.DisplayName)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.Member, row.DisplayName, row.Email))];
    }

    private async Task<bool> IsLastAdminAsync(Guid orgId, Guid exceptMemberId, CancellationToken cancellationToken)
        => !await database.Members
            .AsNoTracking()
            .Where(row => row.OrgId == orgId && row.Id != exceptMemberId)
            .AnyAsync(row => row.Roles.Contains(MemberRole.Admin), cancellationToken);

    private static MemberResponse Describe(
        Member member,
        string displayName,
        string email,
        MemberSnapshot viewer) => new()
        {
            Id = member.Id.ToString(),
            DisplayName = displayName,
            Email = email,
            Roles = [.. member.Roles.Select(role => role.ToApi())],
            CanCreateRepo = member.HasCapability(MemberCapability.CanCreateRepo),
            JoinedAt = member.CreatedAt,
            IsYou = member.Id == viewer.MemberId,
        };

    private static CommandOutcome Refuse()
        => CommandOutcome.Forbidden("members and roles belong to administrators");
}
