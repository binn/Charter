using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Charter.Data;

/// <summary>The outcome of presenting an invitation token.</summary>
/// <param name="Rejection">Why it was refused, or <see cref="InvitationRejection.None"/>.</param>
/// <param name="Invitation">The row, when one matched. Never carries a token.</param>
public sealed record InvitationRedemption(InvitationRejection Rejection, Invitation? Invitation)
{
    /// <summary>Whether the caller may now create or link the account.</summary>
    public bool Accepted => Rejection == InvitationRejection.None;
}

/// <summary>
/// Issues and redeems the invitations of section 30.2.
/// </summary>
/// <remarks>
/// <para>
/// Redemption is a single conditional <c>UPDATE … RETURNING</c> rather than a read, a check and a
/// write. Single-use has to hold against two clicks on the same link at the same instant, and only
/// the database can decide that: the row moves from unconsumed to consumed once, and the second
/// caller updates nothing and is told the invitation is spent.
/// </para>
/// <para>
/// Nothing here ever sees a token except to hash it. The caller mints one with
/// <see cref="Invitation.MintToken"/>, emails the token, and stores the digest.
/// </para>
/// </remarks>
public sealed class InvitationStore
{
    /// <summary>
    /// Spend it, if it can be spent. Every condition is in the <c>WHERE</c>, so the row itself is
    /// the lock.
    /// </summary>
    internal const string RedeemSql = """
        UPDATE invitations
        SET consumed_at = @now, consumed_by_user_id = @user_id
        WHERE token_hash = @token_hash
          AND consumed_at IS NULL
          AND revoked_at IS NULL
          AND expires_at > @now
        RETURNING id
        """;

    private readonly CharterDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    public InvitationStore(CharterDbContext db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Issues an invitation and returns the token to email. The token is not stored and cannot be
    /// recovered afterwards; a lost invitation is reissued, never looked up.
    /// </summary>
    public async Task<(Invitation Invitation, string Token)> IssueAsync(
        Guid orgId,
        string email,
        Guid invitedByUserId,
        IEnumerable<MemberRole> roles,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var (token, hash) = Invitation.MintToken();

        var invitation = Invitation.Issue(
            orgId,
            email,
            invitedByUserId,
            roles,
            hash,
            lifetime,
            _clock.GetUtcNow());

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (invitation, token);
    }

    /// <summary>What is still outstanding for one address, newest first. For the settings list.</summary>
    public async Task<IReadOnlyList<Invitation>> OutstandingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        return await _db.Invitations
            .AsNoTracking()
            .Where(invitation => invitation.OrgId == orgId
                && invitation.ConsumedAt == null
                && invitation.RevokedAt == null
                && invitation.ExpiresAt > now)
            .OrderByDescending(invitation => invitation.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Redeems <paramref name="token"/> for <paramref name="userId"/>, exactly once.
    /// </summary>
    public async Task<InvitationRedemption> RedeemAsync(
        string token,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new InvitationRedemption(InvitationRejection.NotFound, null);
        }

        var hash = Invitation.HashToken(token);
        var now = _clock.GetUtcNow();

        await using var command = await RawSqlCommand
            .CreateAsync(_db, RedeemSql, cancellationToken)
            .ConfigureAwait(false);

        command.AddParameter("token_hash", NpgsqlDbType.Text, hash);
        command.AddParameter("user_id", NpgsqlDbType.Uuid, userId);
        command.AddParameter("now", NpgsqlDbType.TimestampTz, now);

        var redeemed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // The row that was found by hash regardless of whether the update matched: it is what says
        // *why* a refusal happened, and it is read after the update so a race reports "already used"
        // rather than "expired".
        var invitation = await _db.Invitations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (redeemed is Guid)
        {
            return new InvitationRedemption(InvitationRejection.None, invitation);
        }

        if (invitation is null)
        {
            return new InvitationRedemption(InvitationRejection.NotFound, null);
        }

        var rejection = invitation.IsConsumed
            ? InvitationRejection.AlreadyUsed
            : invitation.RevokedAt is not null
                ? InvitationRejection.Revoked
                : InvitationRejection.Expired;

        return new InvitationRedemption(rejection, invitation);
    }

    /// <summary>Withdraws an outstanding invitation. A spent one is left as the audit record it is.</summary>
    public async Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var invitation = await _db.Invitations
            .FirstOrDefaultAsync(candidate => candidate.Id == invitationId, cancellationToken)
            .ConfigureAwait(false);

        if (invitation is null || invitation.IsConsumed)
        {
            return false;
        }

        invitation.Revoke(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Drops invitations nobody spent, once they are well past their expiry.
    /// </summary>
    /// <remarks>
    /// Consumed rows are kept: "who invited this person, and when did they accept" is an audit
    /// question. An unspent token is not, and a dead credential that lingers is a dead credential
    /// somebody has to reason about.
    /// </remarks>
    public async Task<int> PruneExpiredAsync(
        Guid orgId,
        TimeSpan grace,
        CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow() - grace;

        return await _db.Invitations
            .Where(invitation => invitation.OrgId == orgId
                && invitation.ConsumedAt == null
                && invitation.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
