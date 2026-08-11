using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Accounts;

/// <summary>
/// The audit log, read back (section 7.1: admins see it; section 7.3, guardrail 5).
/// </summary>
/// <remarks>
/// <para>
/// A write-only audit log is a compliance ornament. The question this screen answers is the one that
/// gets asked after something goes wrong — "who made this repository requestable", "who made them an
/// engineer" — so entries carry the acting human's name, not just their id, and a plain sentence
/// alongside the dotted verb.
/// </para>
/// <para>
/// Administrators only, and no filter parameters in v1. Section 7.2a gives an instance one
/// organisation, so there is exactly one log; a page of the most recent entries answers the question
/// people actually arrive with, and search can be added when somebody has one this does not answer.
/// </para>
/// </remarks>
public sealed class AuditQueryService
{
    /// <summary>How many entries one page carries.</summary>
    public const int PageSize = 100;

    /// <summary>Metadata values longer than this are dropped from <c>details</c>.</summary>
    /// <remarks>
    /// The audit log holds structured payloads as well as short facts — the recon proposal, for one.
    /// Those belong to the screen that renders them, not to a list of what happened, and a wall of
    /// JSON is how an audit screen stops being read.
    /// </remarks>
    private const int LongestDetail = 120;

    private readonly CharterDbContext database;

    public AuditQueryService(CharterDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary>The most recent entries, newest first. Administrators only.</summary>
    public async Task<(CommandOutcome Outcome, AuditLogResponse? Log)> ListAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (CommandOutcome.Forbidden("the audit log belongs to administrators"), null);
        }

        // One more than a page, so "there is older history" is a fact rather than a guess.
        var rows = await database.AuditLogs
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId)
            .OrderByDescending(row => row.CreatedAt)
            .Take(PageSize + 1)
            .ToListAsync(cancellationToken);

        // The actors, in one further read rather than an outer join. An entry with no actor is
        // Charter itself, and a join that dropped those would hide exactly the rows section 7.3
        // wants visible: the ones nobody is named for.
        var actorIds = rows
            .Where(row => row.ActorUserId.HasValue)
            .Select(row => row.ActorUserId!.Value)
            .Distinct()
            .ToList();

        var actors = await database.Users
            .AsNoTracking()
            .Where(row => actorIds.Contains(row.Id))
            .Select(row => new { row.Id, row.DisplayName, row.Email })
            .ToListAsync(cancellationToken);

        var people = actors.ToDictionary(row => row.Id);

        return (
            CommandOutcome.Ok(),
            new AuditLogResponse
            {
                Entries =
                [
                    .. rows.Take(PageSize).Select(row =>
                    {
                        var actor = row.ActorUserId is { } id ? people.GetValueOrDefault(id) : null;

                        return Describe(row, actor?.DisplayName, actor?.Email);
                    }),
                ],
                HasMore = rows.Count > PageSize,
            });
    }

    private static AuditEntryResponse Describe(AuditLog entry, string? actorName, string? actorEmail)
    {
        var metadata = Metadata(entry);

        var details = metadata
            .Where(pair => pair.Value is { Length: > 0 and <= LongestDetail })
            .Take(6)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

        return new AuditEntryResponse
        {
            Id = entry.Id.ToString(),
            At = entry.CreatedAt,
            Action = entry.Action,
            Summary = Summarise(entry.Action, actorName, metadata),
            ActorName = actorName,
            ActorEmail = actorEmail,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Details = details.Count == 0 ? null : details,
        };
    }

    /// <summary>
    /// The dotted verb as a sentence.
    /// </summary>
    /// <remarks>
    /// An unknown verb falls back to the verb itself rather than to "something happened". A verb this
    /// method has not been taught is a gap in this method, and printing it is how somebody notices.
    /// </remarks>
    private static string Summarise(string action, string? actorName, IReadOnlyDictionary<string, string?> metadata)
    {
        var who = actorName ?? "Charter";
        var role = metadata.GetValueOrDefault("role");
        var email = metadata.GetValueOrDefault("member_email") ?? metadata.GetValueOrDefault("email");
        var repository = metadata.GetValueOrDefault("full_name");

        return action switch
        {
            AuditActions.SetupCompleted => $"{who} claimed this instance and became its first administrator.",
            AuditActions.SignedIn => $"{who} signed in.",
            AuditActions.SignInFailed => "A sign-in was refused.",
            AuditActions.SignedOut => $"{who} signed out.",
            AuditActions.IdentityLinked => $"{who} linked another sign-in provider.",
            AuditActions.PasswordChanged => $"{who} changed a password.",
            AuditActions.PasswordResetRequested => $"{who} had a one-time password-reset link minted.",
            AuditActions.MemberInvited => $"{who} invited {email ?? "somebody"} to the organisation.",
            AuditActions.MemberInviteRevoked => $"{who} withdrew an invitation.",
            AuditActions.MemberInviteAccepted => $"{who} accepted an invitation and created an account.",
            AuditActions.MemberRoleGranted => $"{who} made {email ?? "a member"} {Article(role)}.",
            AuditActions.MemberRoleRevoked => $"{who} removed the {role ?? "role"} role from {email ?? "a member"}.",
            AuditActions.RepoScopeGranted => $"{who} let somebody file requests against a repository.",
            AuditActions.RepoScopeRevoked => $"{who} withdrew somebody's access to a repository.",
            AuditActions.RepoCreationAuthorized => $"{who} was authorised to create a repository.",
            AuditActions.SpecApproved => $"{who} approved the spend on a request.",
            AuditActions.SessionAutoDispatched => "A request was dispatched automatically by policy.",
            ApiAuditActions.SessionHandedOff => $"{who} took over a session's branch.",
            ApiAuditActions.SessionSteered => $"{who} sent a running session a new instruction.",
            ApiAuditActions.SessionRevised => $"{who} revised a spec and rebuilt it.",
            ApiAuditActions.SessionApproved => $"{who} marked a session reviewed.",
            ApiAuditActions.RunnerPairingTokenIssued => $"{who} generated a runner pairing token.",
            ApiAuditActions.RunnerRevoked => $"{who} revoked a runner agent.",
            ApiAuditActions.SetupChecklistDismissed => $"{who} dismissed the setup checklist.",
            OnboardingAuditActions.RepoConnected =>
                $"{who} connected {repository ?? "a repository"}. It was requestable by nobody.",
            OnboardingAuditActions.ReconStarted => $"{who} started a read-only recon run.",
            OnboardingAuditActions.ScopeProposed => "Recon finished and proposed a scope config.",
            OnboardingAuditActions.ScopeConfirmed => $"{who} confirmed the scope and queued the smoke test.",
            OnboardingAuditActions.RepoReady => "The smoke test passed; the repository became requestable.",
            OnboardingAuditActions.SmokeTestFailed => "The smoke test failed.",
            OnboardingAuditActions.PrimerPublished => $"{who} published the repository primer.",
            OnboardingAuditActions.MergeGateChecked => "The merge gate was checked against the provider.",
            OnboardingAuditActions.RepoStatusChanged => $"{who} enabled or disabled a repository.",
            _ => action,
        };
    }

    private static string Article(string? role) => role switch
    {
        null or "" => "a member",
        "admin" => "an administrator",
        "approver" => "an approver",
        "engineer" => "an engineer",
        "requester" => "a requester",
        _ => $"a {role}",
    };

    private static IReadOnlyDictionary<string, string?> Metadata(AuditLog entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Metadata))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(entry.Metadata)
                   ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }
}
