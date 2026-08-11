using Charter.Api.Accounts;
using Charter.Api.Contracts;
using Charter.Auth;
using Charter.Auth.Providers;
using Charter.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Invitations (section 30.2) and the two halves of a password reset (section 21, change spec 001
/// part C.1), against a real Postgres.
/// </summary>
/// <remarks>
/// These are the routes that create and re-credential humans, so the assertions are about the three
/// properties that make them safe rather than about the happy path: single use, the audience rule
/// for a surfaced link, and an audit entry naming who did it.
/// </remarks>
public class AuthAccountInvitationTests
{
    [Fact]
    public async Task AnInvitationCreatesAnAccountExactlyOnce()
    {
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("newcomer");

        var (accepted, userId) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody
            {
                Token = token,
                DisplayName = "New Comer",
                Password = AuthWorld.Password,
            },
            TestContext.Current.CancellationToken);

        Assert.True(accepted.Succeeded);
        Assert.NotEqual(Guid.Empty, userId);

        // The account, the membership and the password identity all exist.
        Assert.True(await world.Db.Users.AnyAsync(row => row.Id == userId, TestContext.Current.CancellationToken));

        var member = await world.Db.Members.SingleAsync(
            row => row.UserId == userId && row.OrgId == world.OrgId,
            TestContext.Current.CancellationToken);

        Assert.Equal([MemberRole.Requester], member.Roles);

        Assert.True(await world.Db.Identities.AnyAsync(
            row => row.UserId == userId && row.Provider == IdentityProviderKind.Password,
            TestContext.Current.CancellationToken));

        // Single use means single use: the second click creates nothing.
        var (again, second) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody
            {
                Token = token,
                DisplayName = "Impostor",
                Password = AuthWorld.Password,
            },
            TestContext.Current.CancellationToken);

        Assert.False(again.Succeeded);
        Assert.Equal(Guid.Empty, second);
        Assert.Contains("already been used", again.Reason, StringComparison.Ordinal);

        Assert.Equal(
            1,
            await world.Db.Users.CountAsync(
                row => row.Email == world.Address("newcomer"),
                TestContext.Current.CancellationToken));

        // Section 7.3, guardrail 5: both ends of it are attributable.
        var audit = await world.AuditAsync();
        Assert.Contains(audit, entry => entry.Action == AuditActions.MemberInvited);
        Assert.Contains(audit, entry => entry.Action == AuditActions.MemberInviteAccepted);
    }

    [Fact]
    public async Task TheInvitedRolesAreWhatTheMemberEndsUpWith()
    {
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("engineer", ApiRole.Engineer, ApiRole.Approver);

        var (outcome, userId) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody
            {
                Token = token,
                DisplayName = "Ellis Engineer",
                Password = AuthWorld.Password,
            },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var member = await world.Db.Members.SingleAsync(
            row => row.UserId == userId,
            TestContext.Current.CancellationToken);

        Assert.Equal([MemberRole.Approver, MemberRole.Engineer], member.Roles.Order());
    }

    [Fact]
    public async Task AnInvitationWithNoRolesNamedIsARequesterAndNothingElse()
    {
        // Section 7.1 roles are additive, so the floor has to be the least a member can be rather
        // than everything.
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("floor");

        var (_, userId) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody { Token = token, DisplayName = "Flo Or", Password = AuthWorld.Password },
            TestContext.Current.CancellationToken);

        var member = await world.Db.Members.SingleAsync(
            row => row.UserId == userId,
            TestContext.Current.CancellationToken);

        Assert.Equal([MemberRole.Requester], member.Roles);
    }

    [Fact]
    public async Task AShortPasswordIsRefusedBeforeTheInvitationIsSpent()
    {
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("careful");

        var (refused, _) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody { Token = token, DisplayName = "Cara Careful", Password = "short" },
            TestContext.Current.CancellationToken);

        Assert.False(refused.Succeeded);

        // The typo did not cost them their invitation.
        var (accepted, userId) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody
            {
                Token = token,
                DisplayName = "Cara Careful",
                Password = AuthWorld.Password,
            },
            TestContext.Current.CancellationToken);

        Assert.True(accepted.Succeeded);
        Assert.NotEqual(Guid.Empty, userId);
    }

    [Fact]
    public async Task ARevokedInvitationCreatesNothing()
    {
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("withdrawn");

        var invitation = await world.Db.Invitations
            .AsNoTracking()
            .SingleAsync(row => row.Email == world.Address("withdrawn"), TestContext.Current.CancellationToken);

        Assert.True((await world.Accounts.RevokeInvitationAsync(
            world.Admin,
            invitation.Id,
            TestContext.Current.CancellationToken)).Succeeded);

        var (outcome, _) = await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody
            {
                Token = token,
                DisplayName = "Withdrawn Person",
                Password = AuthWorld.Password,
            },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("withdrawn", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithEmailOffTheLinkComesBackForTheAdminToPassOn()
    {
        // Change spec 001 part C.1: admins create users directly with a one-time link surfaced in
        // the UI. That is what makes CHARTER_EMAIL_PROVIDER=none a usable configuration.
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = false;

        var (outcome, issued) = await world.Accounts.InviteAsync(
            world.Admin,
            new InviteMemberBody { Email = world.Address("offline") },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(issued);
        Assert.False(issued.Delivery.Emailed);
        Assert.NotNull(issued.Delivery.Link);
        Assert.Contains(AccountService.AcceptInvitationPath, issued.Delivery.Link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithEmailOnTheLinkIsNotInTheResponseAtAll()
    {
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = true;

        var (_, issued) = await world.Accounts.InviteAsync(
            world.Admin,
            new InviteMemberBody { Email = world.Address("online") },
            TestContext.Current.CancellationToken);

        Assert.True(issued!.Delivery.Emailed);
        Assert.Null(issued.Delivery.Link);
        Assert.Single(world.Sender.Sent);
    }

    [Fact]
    public async Task OnlyAnAdminMayInviteListOrRevoke()
    {
        await using var world = await AuthWorld.CreateAsync();

        var requester = world.MemberOf(MemberRole.Requester, MemberRole.Engineer);

        var (invite, _) = await world.Accounts.InviteAsync(
            requester,
            new InviteMemberBody { Email = world.Address("nope") },
            TestContext.Current.CancellationToken);

        var (list, _) = await world.Accounts.ListInvitationsAsync(requester, TestContext.Current.CancellationToken);

        var revoke = await world.Accounts.RevokeInvitationAsync(
            requester,
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.False(invite.Succeeded);
        Assert.False(list.Succeeded);
        Assert.False(revoke.Succeeded);
        Assert.Equal(StatusCodes.Status403Forbidden, invite.Status);
    }

    [Fact]
    public async Task InvitingAnAddressThatAlreadyHasAnAccountSaysSo()
    {
        await using var world = await AuthWorld.CreateAsync();

        var (outcome, issued) = await world.Accounts.InviteAsync(
            world.Admin,
            new InviteMemberBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(issued);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
    }

    [Fact]
    public async Task TheListNeverCarriesATokenAndOnlyShowsWhatIsOutstanding()
    {
        await using var world = await AuthWorld.CreateAsync();

        var token = await world.InviteAsync("listed");

        var (outcome, listed) = await world.Accounts.ListInvitationsAsync(
            world.Admin,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var body = await ApiPayloads.RenderAsync(listed);

        Assert.Contains(world.Address("listed"), body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", ApiPayloads.Keys(body));

        // Once accepted it is not outstanding any more.
        await world.Accounts.AcceptInvitationAsync(
            new AcceptInvitationBody { Token = token, DisplayName = "L Isted", Password = AuthWorld.Password },
            TestContext.Current.CancellationToken);

        var (_, after) = await world.Accounts.ListInvitationsAsync(
            world.Admin,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(after!.Invitations, row => row.Email == world.Address("listed"));
    }
}

/// <summary>Password reset, and the one rule that keeps the public form from being a takeover.</summary>
public class AuthAccountPasswordResetTests
{
    [Fact]
    public async Task ForgotPasswordNeverReturnsALinkEvenWithEmailOff()
    {
        // Anybody can type anybody's address into that form. Surfacing the link there would turn
        // "email is not configured" into "account takeover by anyone who knows an address".
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = false;

        var response = await world.Accounts.ForgotPasswordAsync(
            new ForgotPasswordBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        var body = await ApiPayloads.RenderAsync(response);

        // Not merely null — the response type has nowhere to put one, and the bytes prove it.
        Assert.DoesNotContain("link", ApiPayloads.Keys(body), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AccountService.ResetPasswordPath, body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForgotPasswordSaysTheSameThingForAnAddressWithNoAccount()
    {
        await using var world = await AuthWorld.CreateAsync();

        var known = await world.Accounts.ForgotPasswordAsync(
            new ForgotPasswordBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        var unknown = await world.Accounts.ForgotPasswordAsync(
            new ForgotPasswordBody { Email = $"nobody-{Guid.CreateVersion7():N}@example.test" },
            TestContext.Current.CancellationToken);

        Assert.Equal(known.Message, unknown.Message);
        Assert.Equal(AccountService.ForgotPasswordAcknowledgement, unknown.Message);
    }

    [Fact]
    public async Task AnAdminGetsTheLinkWhenEmailIsOffAndTheResetWorksOnce()
    {
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = false;

        var (outcome, link) = await world.Accounts.AdminPasswordResetAsync(
            world.Admin,
            new AdminPasswordResetBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(link);
        Assert.False(link.Emailed);
        Assert.NotNull(link.Link);

        var token = TokenFrom(link.Link);

        var (reset, userId) = await world.Accounts.ResetPasswordAsync(
            new ResetPasswordBody { Token = token, Password = "a-brand-new-password" },
            TestContext.Current.CancellationToken);

        Assert.True(reset.Succeeded);
        Assert.Equal(world.AdminUserId, userId);

        // The new password works and the old one does not.
        Assert.IsType<SignInResult.Succeeded>(await world.SignInAsync(world.AdminEmail, "a-brand-new-password"));
        Assert.IsType<SignInResult.Failed>(await world.SignInAsync(world.AdminEmail, AuthWorld.Password));

        // And the link is spent: the signature was bound to the verifier it replaced.
        var (twice, _) = await world.Accounts.ResetPasswordAsync(
            new ResetPasswordBody { Token = token, Password = "yet-another-password" },
            TestContext.Current.CancellationToken);

        Assert.False(twice.Succeeded);
        Assert.Contains("already been used", twice.Reason, StringComparison.Ordinal);

        Assert.Contains(
            await world.AuditAsync(),
            entry => entry.Action == AuditActions.PasswordChanged && entry.ActorUserId == world.AdminUserId);
    }

    [Fact]
    public async Task AnAdminResetIsAttributedToTheAdminWhoAskedForIt()
    {
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = false;

        await world.Accounts.AdminPasswordResetAsync(
            world.Admin,
            new AdminPasswordResetBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            await world.AuditAsync(),
            row => row.Action == AuditActions.PasswordResetRequested);

        Assert.Equal(world.AdminUserId, entry.ActorUserId);
    }

    [Fact]
    public async Task ASelfServiceResetIsRecordedWithNoActorBecauseNobodyHasProvenWhoTheyAre()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Accounts.ForgotPasswordAsync(
            new ForgotPasswordBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            await world.AuditAsync(),
            row => row.Action == AuditActions.PasswordResetRequested);

        Assert.Null(entry.ActorUserId);
    }

    [Fact]
    public async Task OnlyAnAdminMayResetSomebodyElsesPassword()
    {
        await using var world = await AuthWorld.CreateAsync();

        var (outcome, link) = await world.Accounts.AdminPasswordResetAsync(
            world.MemberOf(MemberRole.Engineer),
            new AdminPasswordResetBody { Email = world.AdminEmail },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(link);
    }

    [Fact]
    public async Task AnAdminResetForAnAddressWithNoAccountIsANotFoundRatherThanALink()
    {
        await using var world = await AuthWorld.CreateAsync();
        world.Sender.Enabled = false;

        var (outcome, link) = await world.Accounts.AdminPasswordResetAsync(
            world.Admin,
            new AdminPasswordResetBody { Email = $"ghost-{Guid.CreateVersion7():N}@example.test" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(link);
        Assert.Equal(StatusCodes.Status404NotFound, outcome.Status);
    }

    [Fact]
    public async Task AForgedResetTokenSetsNothing()
    {
        await using var world = await AuthWorld.CreateAsync();

        var forged = world.Resets.Issue(world.AdminUserId, "a-hash-this-account-never-had");

        var (outcome, _) = await world.Accounts.ResetPasswordAsync(
            new ResetPasswordBody { Token = forged, Password = "a-brand-new-password" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.IsType<SignInResult.Succeeded>(await world.SignInAsync(world.AdminEmail, AuthWorld.Password));
    }

    private static string TokenFrom(string link) => AuthWorld.TokenFrom(link);
}
