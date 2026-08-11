using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Api.Accounts;

/// <summary>
/// Everything that creates or re-credentials a human: invitations, and the two halves of a password
/// reset (sections 30.2, 21).
/// </summary>
/// <remarks>
/// <para>
/// Three rules run through all of it, and every method below is arranged around them.
/// </para>
/// <para>
/// <strong>A credential is minted once and never read back.</strong> The invitation row holds a
/// digest, and the reset link is a signature rather than a stored token — so the only moment either
/// value exists is the response that created it. There is deliberately no endpoint that returns an
/// outstanding link.
/// </para>
/// <para>
/// <strong>Who is looking at the screen decides what they are shown.</strong> An administrator who
/// just created an account may see its one-time link, because they were entitled to create the
/// account at all. The anonymous visitor at the forgot-password form may not, because anybody can
/// type anybody's address into it — <see cref="OneTimeLinkAudience"/> is where that boundary lives
/// and this type never second-guesses it.
/// </para>
/// <para>
/// <strong>Every one of these is attributable.</strong> Inviting, revoking, accepting and resetting
/// all write an audit entry naming the human responsible (section 7.3, guardrail 5).
/// </para>
/// </remarks>
public sealed class AccountService
{
    /// <summary>Where the invitation link lands in the SPA.</summary>
    public const string AcceptInvitationPath = "/accept-invitation";

    /// <summary>Where the reset link lands in the SPA.</summary>
    public const string ResetPasswordPath = "/reset-password";

    /// <summary>
    /// The one answer the forgot-password form ever gives.
    /// </summary>
    /// <remarks>
    /// Identical for an address with an account and for one without. Anything else turns the form
    /// into a way of asking "does this person work here".
    /// </remarks>
    public const string ForgotPasswordAcknowledgement =
        "If that address has an account, a reset link is on its way. Check your spam folder if it "
        + "does not arrive shortly.";

    private readonly CharterDbContext database;
    private readonly InvitationStore invitations;
    private readonly IAccountMailer mailer;
    private readonly PasswordIdentityProvider passwords;
    private readonly PasswordResetTokens resets;
    private readonly IAuditWriter audit;
    private readonly CharterConfig config;
    private readonly TimeProvider clock;
    private readonly ILogger<AccountService> logger;

    public AccountService(
        CharterDbContext database,
        InvitationStore invitations,
        IAccountMailer mailer,
        PasswordIdentityProvider passwords,
        PasswordResetTokens resets,
        IAuditWriter audit,
        CharterConfig config,
        TimeProvider clock,
        ILogger<AccountService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(mailer);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(resets);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        this.database = database;
        this.invitations = invitations;
        this.mailer = mailer;
        this.passwords = passwords;
        this.resets = resets;
        this.audit = audit;
        this.config = config;
        this.clock = clock;
        this.logger = logger;
    }

    /// <summary>The outstanding invitations, newest first. Admin only, and never a token.</summary>
    public async Task<(CommandOutcome Outcome, InvitationsResponse? Invitations)> ListInvitationsAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (Refuse("inviting people is an administrator action"), null);
        }

        var outstanding = await invitations.OutstandingAsync(member.OrgId, cancellationToken);

        return (
            CommandOutcome.Ok(),
            new InvitationsResponse
            {
                Invitations = [.. outstanding.Select(Describe)],
                EmailEnabled = mailer.Availability.Enabled,
            });
    }

    /// <summary>Invites somebody (section 30.2). Admin only.</summary>
    public async Task<(CommandOutcome Outcome, InvitationIssuedResponse? Issued)> InviteAsync(
        MemberSnapshot member,
        InviteMemberBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (Refuse("inviting people is an administrator action"), null);
        }

        if (!EmailAddress.TryCreate(body.Email, displayName: null, out var address) || address is null)
        {
            return (CommandOutcome.Invalid("Enter the email address to invite."), null);
        }

        var email = address.Address.ToLowerInvariant();

        if (await database.Users.AnyAsync(row => row.Email == email, cancellationToken))
        {
            // Not a 500 and not a silent success: an admin who typed an address that already has an
            // account needs to know that, because the fix is a role change rather than an invitation.
            return (
                CommandOutcome.Conflict($"{email} already has an account on this instance."),
                null);
        }

        var roles = ToRoles(body.Roles);

        var (invitation, token) = await invitations.IssueAsync(
            member.OrgId,
            email,
            member.UserId,
            roles,
            lifetime: null,
            cancellationToken);

        var organizationName = await database.Organizations
            .AsNoTracking()
            .Where(row => row.Id == member.OrgId)
            .Select(row => row.Name)
            .SingleOrDefaultAsync(cancellationToken);

        var inviterName = await database.Users
            .AsNoTracking()
            .Where(row => row.Id == member.UserId)
            .Select(row => row.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);

        var delivery = await mailer.SendInvitationAsync(
            address,
            new InvitationEmail
            {
                InviterName = inviterName ?? "An administrator",
                OrganizationName = organizationName ?? "this Charter instance",
                AcceptUrl = LinkTo(AcceptInvitationPath, token),
                ExpiresAt = invitation.ExpiresAt,
            },

            // Section 30.2 with email off: the admin who created the invitation is the one person
            // entitled to carry the link, and they are standing right here.
            OneTimeLinkAudience.Administrator,
            cancellationToken);

        await audit.RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = member.UserId,
                Action = AuditActions.MemberInvited,
                TargetType = nameof(Invitation),
                TargetId = invitation.Id.ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["email"] = email,
                    ["roles"] = string.Join(',', roles),
                    ["emailed"] = delivery.Emailed.ToString(),
                },
            },
            cancellationToken);

        return (
            CommandOutcome.Ok(),
            new InvitationIssuedResponse
            {
                Invitation = Describe(invitation),
                Delivery = Surface(delivery),
            });
    }

    /// <summary>Withdraws an invitation nobody has spent. Admin only.</summary>
    public async Task<CommandOutcome> RevokeInvitationAsync(
        MemberSnapshot member,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!member.HasRole(MemberRole.Admin))
        {
            return Refuse("withdrawing an invitation is an administrator action");
        }

        // Scoped to this organisation before anything is written: section 7.2a has one org per
        // instance, but an id from a request body is still input.
        var belongs = await database.Invitations
            .AsNoTracking()
            .AnyAsync(row => row.Id == invitationId && row.OrgId == member.OrgId, cancellationToken);

        if (!belongs || !await invitations.RevokeAsync(invitationId, cancellationToken))
        {
            return CommandOutcome.NotFound();
        }

        await audit.RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = member.UserId,
                Action = AuditActions.MemberInviteRevoked,
                TargetType = nameof(Invitation),
                TargetId = invitationId.ToString(),
            },
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Redeems an invitation and creates the account (section 30.2). Anonymous, and single-use.
    /// </summary>
    /// <returns>The new user, for the caller to sign in.</returns>
    public async Task<(CommandOutcome Outcome, Guid UserId)> AcceptInvitationAsync(
        AcceptInvitationBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.Token))
        {
            return (CommandOutcome.Invalid(UnusableInvitation), Guid.Empty);
        }

        var hash = Invitation.HashToken(body.Token);

        // Read first so the account this redemption belongs to is known before the row is spent:
        // `consumed_by_user_id` has to name the person who actually accepted, and the conditional
        // UPDATE below is still what makes it single-use against two simultaneous clicks.
        var pending = await database.Invitations
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.TokenHash == hash, cancellationToken);

        if (pending is null)
        {
            return (CommandOutcome.Invalid(UnusableInvitation), Guid.Empty);
        }

        var existing = await database.Users
            .SingleOrDefaultAsync(row => row.Email == pending.Email, cancellationToken);

        var password = Secret.From(body.Password);

        if (existing is null)
        {
            if (password is null || !CharterPasswordHasher.IsAcceptable(password))
            {
                // Checked before the token is spent, so a short password does not cost somebody
                // their invitation.
                return (
                    CommandOutcome.Invalid(
                        $"Choose a password of at least {CharterPasswordHasher.MinimumPasswordLength} characters."),
                    Guid.Empty);
            }

            if (string.IsNullOrWhiteSpace(body.DisplayName))
            {
                return (CommandOutcome.Invalid("Enter the name you want to be known by."), Guid.Empty);
            }
        }

        var userId = existing?.Id ?? Guid.CreateVersion7();
        var now = clock.GetUtcNow();

        // One transaction around the whole redemption.
        //
        // The order is forced: `invitations.consumed_by_user_id` references `users`, so the account
        // has to exist before the row can be spent — and creating an account on the strength of a
        // token that turns out to be spent, revoked or expired is exactly the hole this endpoint
        // must not have. Inside a transaction the two are one act: a refused redemption rolls the
        // account back out of existence, and two simultaneous clicks still resolve to one winner
        // because the conditional UPDATE is the lock.
        var owned = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            if (existing is null)
            {
                database.Users.Add(User.Create(
                    pending.Email,
                    body.DisplayName!.Trim(),
                    TeachingLevel.ExplainEverything,
                    now,
                    userId));

                await database.SaveChangesAsync(cancellationToken);
            }

            var redemption = await invitations.RedeemAsync(body.Token, userId, cancellationToken);

            if (!redemption.Accepted)
            {
                if (owned is not null)
                {
                    await owned.RollbackAsync(cancellationToken);
                }

                // The tracker still holds rows the rollback has undone; leaving them would make the
                // next SaveChanges on this scope re-insert an account nobody redeemed.
                database.ChangeTracker.Clear();

                return (CommandOutcome.Invalid(DescribeRejection(redemption.Rejection)), Guid.Empty);
            }

            var member = await database.Members
                .SingleOrDefaultAsync(row => row.OrgId == pending.OrgId && row.UserId == userId, cancellationToken);

            if (member is null)
            {
                database.Members.Add(Member.Create(pending.OrgId, userId, pending.Roles, now: now));
                await database.SaveChangesAsync(cancellationToken);
            }

            if (password is not null && CharterPasswordHasher.IsAcceptable(password))
            {
                await passwords.SetPasswordAsync(userId, password, cancellationToken);
            }

            await audit.RecordAsync(
                new AuditEntry
                {
                    OrgId = pending.OrgId,
                    ActorUserId = userId,
                    Action = AuditActions.MemberInviteAccepted,
                    TargetType = nameof(Invitation),
                    TargetId = pending.Id.ToString(),
                    Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["invited_by"] = pending.InvitedByUserId.ToString(),
                        ["roles"] = string.Join(',', pending.Roles),
                    },
                },
                cancellationToken);

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (owned is not null)
            {
                await owned.RollbackAsync(cancellationToken);
            }

            database.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }

        logger.LogInformation("An invitation was accepted and a member created");

        return (CommandOutcome.Ok(), userId);
    }

    /// <summary>
    /// The public forgot-password form (section 21).
    /// </summary>
    /// <remarks>
    /// Answers <see cref="ForgotPasswordAcknowledgement"/> whatever happened, and the response type
    /// has nowhere to put a link. Both are load-bearing: the first stops the form being an
    /// enumeration oracle, the second stops "email is off" becoming "anybody may take over any
    /// account by typing its address".
    /// </remarks>
    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(
        ForgotPasswordBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (EmailAddress.TryCreate(body.Email, displayName: null, out var address) && address is not null)
        {
            var email = address.Address.ToLowerInvariant();

            var user = await database.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Email == email, cancellationToken);

            if (user is not null)
            {
                await MintResetAsync(user, address, OneTimeLinkAudience.Recipient, actor: null, cancellationToken);
            }
            else
            {
                logger.LogInformation("A password reset was requested for an address with no account");
            }
        }

        return new ForgotPasswordResponse { Message = ForgotPasswordAcknowledgement };
    }

    /// <summary>
    /// An admin minting a reset link for somebody else (change spec 001 part C.1).
    /// </summary>
    /// <remarks>
    /// This is the route that makes an instance with <c>CHARTER_EMAIL_PROVIDER=none</c> usable: the
    /// link comes back in the response for the admin to pass on, because they were entitled to reset
    /// the account in the first place.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, OneTimeLinkResponse? Link)> AdminPasswordResetAsync(
        MemberSnapshot member,
        AdminPasswordResetBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!member.HasRole(MemberRole.Admin))
        {
            return (Refuse("resetting somebody else's password is an administrator action"), null);
        }

        if (!EmailAddress.TryCreate(body.Email, displayName: null, out var address) || address is null)
        {
            return (CommandOutcome.Invalid("Enter the address of the account to reset."), null);
        }

        var email = address.Address.ToLowerInvariant();

        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Email == email, cancellationToken);

        // An admin may know the account exists, so this one does say so — the enumeration argument
        // applies to the anonymous form, not to somebody who can already list the membership.
        if (user is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var isMember = await database.Members
            .AsNoTracking()
            .AnyAsync(row => row.OrgId == member.OrgId && row.UserId == user.Id, cancellationToken);

        if (!isMember)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var delivery = await MintResetAsync(
            user,
            EmailAddress.Create(user.Email, user.DisplayName),
            OneTimeLinkAudience.Administrator,
            member.UserId,
            cancellationToken);

        return (CommandOutcome.Ok(), Surface(delivery));
    }

    /// <summary>Spends a reset link and sets the new password.</summary>
    /// <returns>The account, for the caller to sign in.</returns>
    public async Task<(CommandOutcome Outcome, Guid UserId)> ResetPasswordAsync(
        ResetPasswordBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!PasswordResetTokens.TryReadSubject(body.Token, out var userId))
        {
            return (
                CommandOutcome.Invalid(PasswordResetTokens.Describe(PasswordResetRejection.Malformed)),
                Guid.Empty);
        }

        var identity = await database.Identities
            .SingleOrDefaultAsync(
                row => row.UserId == userId && row.Provider == IdentityProviderKind.Password,
                cancellationToken);

        var rejection = resets.Verify(body.Token, userId, identity?.SecretHash);

        if (rejection != PasswordResetRejection.None)
        {
            return (CommandOutcome.Invalid(PasswordResetTokens.Describe(rejection)), Guid.Empty);
        }

        var password = Secret.From(body.Password);

        if (password is null || !CharterPasswordHasher.IsAcceptable(password))
        {
            return (
                CommandOutcome.Invalid(
                    $"Choose a password of at least {CharterPasswordHasher.MinimumPasswordLength} characters."),
                Guid.Empty);
        }

        if (!await passwords.SetPasswordAsync(userId, password, cancellationToken))
        {
            return (CommandOutcome.Invalid(PasswordResetTokens.Describe(PasswordResetRejection.Spent)), Guid.Empty);
        }

        var orgId = await database.Members
            .AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.CreatedAt)
            .Select(row => (Guid?)row.OrgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (orgId is { } org)
        {
            await audit.RecordAsync(
                new AuditEntry
                {
                    OrgId = org,
                    ActorUserId = userId,
                    Action = AuditActions.PasswordChanged,
                    TargetType = "user",
                    TargetId = userId.ToString(),
                    Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["via"] = "reset_link",
                    },
                },
                cancellationToken);
        }

        return (CommandOutcome.Ok(), userId);
    }

    private const string UnusableInvitation =
        "That invitation link is not one we recognise. Ask whoever invited you for a new one.";

    private async Task<OneTimeLinkDelivery> MintResetAsync(
        User user,
        EmailAddress address,
        OneTimeLinkAudience audience,
        Guid? actor,
        CancellationToken cancellationToken)
    {
        var currentHash = await database.Identities
            .AsNoTracking()
            .Where(row => row.UserId == user.Id && row.Provider == IdentityProviderKind.Password)
            .Select(row => row.SecretHash)
            .SingleOrDefaultAsync(cancellationToken);

        var token = resets.Issue(user.Id, currentHash);

        var delivery = await mailer.SendPasswordResetAsync(
            address,
            new PasswordResetEmail
            {
                RecipientName = user.DisplayName,
                ResetUrl = LinkTo(ResetPasswordPath, token),
                ValidFor = PasswordResetTokens.Lifetime,
            },
            audience,
            cancellationToken);

        var orgId = await database.Members
            .AsNoTracking()
            .Where(row => row.UserId == user.Id)
            .OrderBy(row => row.CreatedAt)
            .Select(row => (Guid?)row.OrgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (orgId is { } org)
        {
            await audit.RecordAsync(
                new AuditEntry
                {
                    OrgId = org,

                    // Null when the person asked for it themselves and is not yet authenticated;
                    // the admin path names them.
                    ActorUserId = actor,
                    Action = AuditActions.PasswordResetRequested,
                    TargetType = "user",
                    TargetId = user.Id.ToString(),
                    Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["audience"] = audience.ToString().ToLowerInvariant(),
                        ["emailed"] = delivery.Emailed.ToString(),
                    },
                },
                cancellationToken);
        }

        return delivery;
    }

    private Uri LinkTo(string path, string token)
        => new(config.BaseUrl, $"{path}?token={Uri.EscapeDataString(token)}");

    private static OneTimeLinkResponse Surface(OneTimeLinkDelivery delivery) => new()
    {
        Emailed = delivery.Emailed,
        Message = delivery.Explanation,

        // Absent, not null, when the caller may not see it (section 7.4).
        Link = delivery.LinkToSurface?.ToString(),
    };

    private static InvitationResponse Describe(Invitation invitation) => new()
    {
        Id = invitation.Id.ToString(),
        Email = invitation.Email,
        Roles = [.. invitation.Roles.Select(role => role.ToApi())],
        CreatedAt = invitation.CreatedAt,
        ExpiresAt = invitation.ExpiresAt,
    };

    /// <summary>
    /// The roles an invitation grants, defaulting to the least a member can be.
    /// </summary>
    /// <remarks>
    /// Section 7.1 makes roles additive, so an empty list is a member who can do nothing rather than
    /// a member with everything. Requester is the floor, and an admin who meant more says so.
    /// </remarks>
    private static IReadOnlyList<MemberRole> ToRoles(IReadOnlyList<ApiRole>? roles)
        => roles is null or { Count: 0 }
            ? [MemberRole.Requester]
            : [.. roles.Select(role => role.ToDomain()).Distinct().Order()];

    private static string DescribeRejection(InvitationRejection rejection) => rejection switch
    {
        InvitationRejection.AlreadyUsed =>
            "That invitation has already been used. Sign in instead, or ask for a new one.",
        InvitationRejection.Expired =>
            "That invitation has expired. Ask whoever invited you for a new one.",
        InvitationRejection.Revoked =>
            "That invitation was withdrawn. Ask whoever invited you for a new one.",
        _ => UnusableInvitation,
    };

    private static CommandOutcome Refuse(string reason) => CommandOutcome.Forbidden(reason);
}
