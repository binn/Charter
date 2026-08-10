using System.Globalization;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Runners.Agent;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Runners;

/// <summary>Pairing and revocation for Settings → Runners (section 33.3).</summary>
/// <remarks>
/// <para>
/// Both actions are an admin's, and both are audited: a pairing token is a credential about to
/// exist, and a revocation kills work somebody is waiting on. Section 7.3's fifth guardrail wants
/// each attributable to a named human.
/// </para>
/// <para>
/// The agent plane is registered only when <c>CHARTER_RUNNER</c> includes <c>agent</c>, so the
/// dependency is an <see cref="IEnumerable{T}"/> that resolves empty otherwise. An instance running
/// only on GitHub Actions has no agents to pair, and this says so in a sentence rather than failing
/// to start.
/// </para>
/// </remarks>
public sealed class RunnersCommandService
{
    /// <summary>What an instance with no agent plane answers, and what to change about it.</summary>
    public const string AgentPlaneDisabled =
        "This instance is not set up to run Charter Agents. Add `agent` to CHARTER_RUNNER and restart "
        + "Charter, then generate a pairing token.";

    private readonly CharterDbContext database;
    private readonly AgentPlaneService? plane;
    private readonly CharterConfig config;
    private readonly TimeProvider clock;

    public RunnersCommandService(
        CharterDbContext database,
        IEnumerable<AgentPlaneService> plane,
        CharterConfig config,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.plane = plane.FirstOrDefault();
        this.config = config;
        this.clock = clock;
    }

    /// <summary>
    /// Section 33.3 step 1: a single-use, short-TTL pairing token, returned exactly once.
    /// </summary>
    /// <remarks>
    /// The command is assembled here so <c>--server</c> carries the instance's configured base URL.
    /// The browser's own origin will not do: the machine the agent runs on is frequently not the one
    /// the admin is looking at Charter from, and a command that works only from the admin's laptop is
    /// the kind of thing that gets discovered twenty minutes later.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, PairingTokenResponse? Token)> IssuePairingTokenAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (CommandOutcome.Forbidden("registering runners is an administrator action"), null);
        }

        if (plane is null)
        {
            return (CommandOutcome.Conflict(AgentPlaneDisabled), null);
        }

        var now = clock.GetUtcNow();
        var invitation = await plane.InviteAsync(member.OrgId, SuggestName(now), cancellationToken: cancellationToken);

        database.AuditLogs.Add(AuditLog.Record(
            member.OrgId,
            ApiAuditActions.RunnerPairingTokenIssued,
            targetType: "runner_agent",
            actorUserId: member.UserId,

            // The token itself is never recorded — not here, not on the row, which holds a verifier.
            targetId: invitation.AgentId.ToString(),
            now: now));

        await database.SaveChangesAsync(cancellationToken);

        return (
            CommandOutcome.Ok(),
            new PairingTokenResponse
            {
                Token = invitation.PairingToken,
                Command = string.Create(
                    CultureInfo.InvariantCulture,
                    $"charter-agent --server {config.BaseUrl.ToString().TrimEnd('/')} --token {invitation.PairingToken}"),
                ExpiresAt = invitation.ExpiresAt,
            });
    }

    /// <summary>
    /// Section 33.3 step 5: revocation kills in-flight jobs and invalidates the credential, instantly.
    /// </summary>
    public async Task<CommandOutcome> RevokeAsync(
        MemberSnapshot member,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return CommandOutcome.Forbidden("revoking a runner is an administrator action");
        }

        if (plane is null)
        {
            return CommandOutcome.Conflict(AgentPlaneDisabled);
        }

        // Section 7.2a: one instance, one organisation — but the filter is still applied, because an
        // id from outside it must read as "no such agent" rather than as a successful revocation.
        var exists = await database.RunnerAgents
            .AsNoTracking()
            .AnyAsync(row => row.Id == agentId && row.OrgId == member.OrgId, cancellationToken);

        if (!exists)
        {
            return CommandOutcome.NotFound();
        }

        var now = clock.GetUtcNow();

        // The audit row goes in first and is saved by the plane's own transaction boundary, so a
        // revocation that succeeds is never unattributed.
        database.AuditLogs.Add(AuditLog.Record(
            member.OrgId,
            ApiAuditActions.RunnerRevoked,
            targetType: "runner_agent",
            actorUserId: member.UserId,
            targetId: agentId.ToString(),
            now: now));

        await database.SaveChangesAsync(cancellationToken);

        var revoked = await plane.RevokeAsync(
            agentId,
            "Revoked by an administrator.",
            cancellationToken);

        return revoked ? CommandOutcome.Ok() : CommandOutcome.NotFound();
    }

    /// <summary>
    /// A placeholder name, replaced by the agent's own on pairing.
    /// </summary>
    /// <remarks>
    /// Distinct per invitation so two outstanding tokens are two rows a person can tell apart. The
    /// stamp is minutes, not a random string: an admin who generated one at 14:32 recognises it.
    /// </remarks>
    private static string SuggestName(DateTimeOffset now)
        => string.Create(CultureInfo.InvariantCulture, $"Waiting to pair ({now:yyyy-MM-dd HH:mm} UTC)");
}
