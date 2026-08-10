using System.Globalization;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Setup;

/// <summary>
/// <c>GET /api/setup/checklist</c> — section 30.2's persistent admin checklist.
/// </summary>
/// <remarks>
/// <para>
/// <em>"Modal wizards trap people who need to go find a token. A checklist lets them leave and come
/// back."</em> So every row is answered from state that already exists — an organisation's name, a
/// repository's readiness, a usable credential — rather than from a progress counter somebody has to
/// remember to increment. An admin who connects a repository in a different tab and comes back finds
/// the row ticked, because the tick is a query.
/// </para>
/// <para>
/// The rows are returned in section 30.2's order and never sorted by done-ness: reordering a
/// checklist under somebody as they tick things off is disorienting, and the sequence is itself the
/// advice. Nothing is ever disabled either — <see cref="SetupTaskResponse.BlockedBy"/> is an
/// explanation, not a lock.
/// </para>
/// <para>
/// A viewer who is not an admin gets <c>null</c>, which the endpoint sends as a JSON <c>null</c>
/// body rather than a 403. A requester's dashboard has no checklist on it, and that is not an error
/// state the page should have to render — the same reasoning section 7.4 applies to a field, applied
/// to a whole resource.
/// </para>
/// </remarks>
public sealed class SetupChecklistService
{
    private readonly CharterDbContext database;
    private readonly CharterConfig config;
    private readonly TimeProvider clock;

    public SetupChecklistService(CharterDbContext database, CharterConfig config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.config = config;
        this.clock = clock;
    }

    /// <summary>The checklist, or <c>null</c> for anyone who is not an admin.</summary>
    public async Task<SetupChecklistResponse?> DescribeAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return null;
        }

        var tasks = await TasksAsync(member.OrgId, cancellationToken);

        return new SetupChecklistResponse
        {
            Tasks = tasks,
            DismissedAt = await DismissedAtAsync(member.OrgId, cancellationToken),
        };
    }

    /// <summary>
    /// Section 30.2: dismissible <em>once complete</em>.
    /// </summary>
    /// <remarks>
    /// Refusing a partial dismissal is the one rule this endpoint enforces, and it is worth
    /// enforcing on the server: a checklist that can be dismissed at 3 of 7 is a checklist that gets
    /// dismissed at 3 of 7 and never seen again, which loses the four things the instance still
    /// needs.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, SetupChecklistResponse? Checklist)> DismissAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (CommandOutcome.Forbidden("the setup checklist belongs to administrators"), null);
        }

        var tasks = await TasksAsync(member.OrgId, cancellationToken);
        var outstanding = tasks.Count(task => !task.Done);

        if (outstanding > 0)
        {
            return (
                CommandOutcome.Conflict(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"there {(outstanding == 1 ? "is" : "are")} still {outstanding} thing"
                        + $"{(outstanding == 1 ? string.Empty : "s")} to set up, so the checklist stays")),
                null);
        }

        var existing = await DismissedAtAsync(member.OrgId, cancellationToken);
        var at = existing ?? clock.GetUtcNow();

        if (existing is null)
        {
            database.AuditLogs.Add(AuditLog.Record(
                member.OrgId,
                ApiAuditActions.SetupChecklistDismissed,
                targetType: "organization",
                actorUserId: member.UserId,
                targetId: member.OrgId.ToString(),
                now: at));

            await database.SaveChangesAsync(cancellationToken);
        }

        return (
            CommandOutcome.Ok(),
            new SetupChecklistResponse { Tasks = tasks, DismissedAt = at });
    }

    private async Task<IReadOnlyList<SetupTaskResponse>> TasksAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var organization = await database.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == orgId, cancellationToken);

        var repos = await database.Repos
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var credentials = await database.CredentialGrants
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .ToListAsync(cancellationToken);

        var budgets = await database.Budgets
            .AsNoTracking()
            .CountAsync(row => row.OrgId == orgId, cancellationToken);

        var members = await database.Members
            .AsNoTracking()
            .CountAsync(row => row.OrgId == orgId, cancellationToken);

        var installed = repos.Where(repo => repo.GithubInstallationId > 0).ToList();
        var ready = repos.Where(repo => repo.Status == RepoStatus.Ready).ToList();
        var usable = credentials.Where(row => row.IsUsableAt(now)).ToList();

        // Section 30.2's list, in section 30.2's order.
        return
        [
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.NameOrganisation,
                Title = "Name your organisation",
                Description = "It appears on every page and in the emails Charter sends.",

                // The seeded placeholder is not a name somebody chose, so it does not tick the row.
                Done = organization is not null
                       && !string.IsNullOrWhiteSpace(organization.Name)
                       && !string.Equals(
                           organization.Name,
                           PersonalOrganizationSeeder.DefaultOrganizationName,
                           StringComparison.Ordinal),
                Href = "/settings",
                DoneSummary = organization?.Name,
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.ConnectGithub,
                Title = "Connect GitHub",
                Description = "Install the Charter GitHub App on the account that owns your repositories.",
                Done = installed.Count > 0,

                // The one destination outside Charter. The App install happens on GitHub, and
                // pretending otherwise would send somebody to a page that cannot do it.
                Href = "https://github.com/apps/charter/installations/new",
                External = true,
                DoneSummary = installed.Count == 0 ? null : $"Installed on {OwnerOf(installed[0].FullName)}",
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.AddModelCredential,
                Title = "Add a model credential",
                Description = "Nothing can be refined or built until Charter can reach a model.",
                Done = usable.Count > 0,
                Href = "/settings",
                DoneSummary = usable.Count == 0 ? null : Describe(usable),
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.ConnectRepository,
                Title = "Connect your first repository",
                Description =
                    "Charter reads it, proposes a scope config, and runs a smoke test before anyone "
                    + "can file against it.",
                Done = ready.Count > 0,
                Href = "/projects",

                // Not a lock. Section 9's flow starts at an installation, so saying where to start is
                // information; the row stays clickable either way.
                BlockedBy = installed.Count > 0 ? null : ApiSetupTaskId.ConnectGithub,
                DoneSummary = ready.Count == 0
                    ? null
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ready.Count} repositor{(ready.Count == 1 ? "y" : "ies")} · {NameOf(ready[0].FullName)}"),
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.SetBudgets,
                Title = "Set budgets",
                Description =
                    "In a small team the monthly cap does more governing than the approval queue ever will.",
                Done = budgets > 0,
                Href = "/settings",
                DoneSummary = budgets == 0
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"{budgets} budget{(budgets == 1 ? string.Empty : "s")}"),
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.InvitePeople,
                Title = "Invite people",
                Description = "Charter is not much use with one account on it.",

                // Section 7.2: personal mode is one member holding every role. Inviting a second
                // person is the only thing that changes, and it is this row.
                Done = members > 1,
                Href = "/settings",
                DoneSummary = members <= 1
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"{members} people"),
            },
            new SetupTaskResponse
            {
                Id = ApiSetupTaskId.NotificationChannels,
                Title = "Choose notification channels",
                Description =
                    "Only two states notify — a question for the requester, and something ready to try.",

                // Section 22 has one channel in v1, so "configured" means Charter can send mail at
                // all. Saying it is done while every notification is silently dropped would be the
                // exact failure change spec 001 C.3 exists to prevent.
                Done = config.Email.Enabled,
                Href = "/settings",
                BlockedBy = members > 1 ? null : ApiSetupTaskId.InvitePeople,
                DoneSummary = config.Email.Enabled
                    ? $"Email · {config.Email.FromAddress ?? config.Email.ProviderToken}"
                    : null,
            },
        ];
    }

    /// <summary>
    /// When the checklist was dismissed, from the audit log.
    /// </summary>
    /// <remarks>
    /// A dismissal is a server-side fact — there is no browser storage in this app — attributable to
    /// one admin and append-only, which is three of the audit log's properties. A column on the
    /// organisation would be a better home; it is an entity this work does not own. See the report.
    /// </remarks>
    private async Task<DateTimeOffset?> DismissedAtAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var dismissal = await database.AuditLogs
            .AsNoTracking()
            .Where(row => row.OrgId == orgId && row.Action == ApiAuditActions.SetupChecklistDismissed)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => (DateTimeOffset?)row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return dismissal;
    }

    private static string Describe(IReadOnlyList<CredentialGrant> credentials)
    {
        var kinds = credentials
            .Select(row => Charter.Data.EnumDbNames<CredentialKind>.ToDb(row.Kind))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return string.Join(" · ", kinds);
    }

    private static string OwnerOf(string fullName)
    {
        var slash = fullName.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? fullName : fullName[..slash];
    }

    private static string NameOf(string fullName)
    {
        var slash = fullName.LastIndexOf('/');
        return slash < 0 ? fullName : fullName[(slash + 1)..];
    }
}
