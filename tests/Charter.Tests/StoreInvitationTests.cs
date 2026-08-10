using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Section 30.2's invitations, on the row rather than in memory.
/// </summary>
/// <remarks>
/// An invitation link creates an account, so it is a credential and is treated like one: the token
/// is never stored, single use is enforced by the database rather than by a check in front of it,
/// and an expired link is refused rather than quietly honoured.
/// </remarks>
public class StoreInvitationTests
{
    [Fact]
    public async Task AnInvitationIsRedeemedExactlyOnce()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invited = await fixture.AddUserAsync("invitee");

        var (invitation, token) = await IssueAsync(fixture, "newhire@example.com");

        var first = await RedeemAsync(fixture, token, invited);
        Assert.True(first.Accepted);
        Assert.Equal(InvitationRejection.None, first.Rejection);
        Assert.NotNull(first.Invitation);
        Assert.Equal(invited, first.Invitation.ConsumedByUserId);
        Assert.Equal(fixture.Clock.GetUtcNow(), first.Invitation.ConsumedAt);

        // The same link, clicked again - a forwarded mail, a browser restoring tabs, a second person
        // in the same inbox. Every condition is in the UPDATE's WHERE clause, so the row itself is
        // what refuses, not a check somebody could forget to write.
        var second = await RedeemAsync(fixture, token, await fixture.AddUserAsync("opportunist"));
        Assert.False(second.Accepted);
        Assert.Equal(InvitationRejection.AlreadyUsed, second.Rejection);

        var stored = await fixture.WithContextAsync(db => db.Invitations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == invitation.Id, TestContext.Current.CancellationToken));

        Assert.Equal(invited, stored.ConsumedByUserId);
    }

    [Fact]
    public async Task AnExpiredInvitationIsRefused()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (_, token) = await IssueAsync(fixture, "slow@example.com", TimeSpan.FromDays(7));

        // Six days later it still works.
        fixture.Clock.Now = fixture.Clock.Now.AddDays(6);
        Assert.Equal(
            InvitationRejection.None,
            (await RedeemAsync(fixture, token, await fixture.AddUserAsync("slow"))).Rejection);

        var (_, stale) = await IssueAsync(fixture, "stale@example.com", TimeSpan.FromDays(7));

        fixture.Clock.Now = fixture.Clock.Now.AddDays(8);

        var refused = await RedeemAsync(fixture, stale, await fixture.AddUserAsync("stale"));

        Assert.False(refused.Accepted);
        Assert.Equal(InvitationRejection.Expired, refused.Rejection);
        Assert.NotNull(refused.Invitation);
        Assert.Null(refused.Invitation.ConsumedAt);
    }

    [Fact]
    public async Task AnUnknownOrWithdrawnTokenIsRefusedAndSaysWhich()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var user = await fixture.AddUserAsync("stranger");

        var (_, invented) = Invitation.MintToken();
        var unknown = await RedeemAsync(fixture, invented, user);

        Assert.Equal(InvitationRejection.NotFound, unknown.Rejection);
        Assert.Null(unknown.Invitation);
        Assert.Equal(
            InvitationRejection.NotFound,
            (await RedeemAsync(fixture, "   ", user)).Rejection);

        var (invitation, token) = await IssueAsync(fixture, "reconsidered@example.com");

        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InvitationStore>();

        Assert.True(await store.RevokeAsync(invitation.Id, TestContext.Current.CancellationToken));

        var revoked = await RedeemAsync(fixture, token, user);
        Assert.Equal(InvitationRejection.Revoked, revoked.Rejection);
    }

    [Fact]
    public async Task TheTokenIsNeverStoredAndTheRowCarriesOnlyItsDigest()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (invitation, token) = await IssueAsync(fixture, "Ada@Example.com");

        Assert.Equal(Invitation.HashToken(token), invitation.TokenHash);
        Assert.Equal(Invitation.TokenHashLength, invitation.TokenHash.Length);
        Assert.DoesNotContain(token, invitation.TokenHash, StringComparison.Ordinal);

        // Normalised the way User.Email is, so the invited address and the account that redeems it
        // compare as the same string.
        Assert.Equal("ada@example.com", invitation.Email);

        // And nothing in the database can be turned back into a link: the whole row, read as text,
        // does not contain the token.
        var columns = await fixture.WithContextAsync(db => db.Invitations
            .AsNoTracking()
            .Where(candidate => candidate.Id == invitation.Id)
            .Select(candidate => candidate.TokenHash + "|" + candidate.Email)
            .SingleAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(token, columns, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutstandingInvitationsAreTheOnesStillWorthChasing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (_, accepted) = await IssueAsync(fixture, "accepted@example.com");
        _ = await IssueAsync(fixture, "waiting@example.com");
        var (_, lapsed) = await IssueAsync(fixture, "lapsed@example.com", TimeSpan.FromHours(1));

        await RedeemAsync(fixture, accepted, await fixture.AddUserAsync("accepted"));

        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);

        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InvitationStore>();

        var outstanding = await store.OutstandingAsync(fixture.OrgId, TestContext.Current.CancellationToken);

        Assert.Equal(["waiting@example.com"], outstanding.Select(invitation => invitation.Email));

        // The lapsed one is still a row until it is swept, and sweeping keeps what somebody accepted:
        // "who invited this person, and when did they accept" is an audit question.
        Assert.Equal(
            1,
            await store.PruneExpiredAsync(fixture.OrgId, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(
            InvitationRejection.NotFound,
            (await RedeemAsync(fixture, lapsed, fixture.UserId)).Rejection);

        var kept = await fixture.WithContextAsync(db => db.Invitations
            .Where(invitation => invitation.OrgId == fixture.OrgId)
            .CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, kept);
    }

    private static async Task<(Invitation Invitation, string Token)> IssueAsync(
        StoreFixture fixture,
        string email,
        TimeSpan? lifetime = null)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InvitationStore>();

        return await store.IssueAsync(
            fixture.OrgId,
            email,
            fixture.UserId,
            [MemberRole.Requester],
            lifetime,
            TestContext.Current.CancellationToken);
    }

    private static async Task<InvitationRedemption> RedeemAsync(StoreFixture fixture, string token, Guid userId)
    {
        // A fresh scope every time, because a redemption is a request and the second click is a
        // second request - possibly on a second replica.
        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InvitationStore>();

        return await store.RedeemAsync(token, userId, TestContext.Current.CancellationToken);
    }
}

/// <summary>The invitation aggregate's own rules, which need no database.</summary>
public class DomainInvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMintedTokenIsHighEntropyAndOnlyItsDigestIsKeepable()
    {
        var (first, firstHash) = Invitation.MintToken();
        var (second, _) = Invitation.MintToken();

        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 40, "A 256-bit token is at least 40 base64url characters.");
        Assert.Equal(Invitation.HashToken(first), firstHash);
        Assert.Equal(Invitation.TokenHashLength, firstHash.Length);

        // Base64url: it has to survive a copy out of a mail client and a paste into a URL.
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void StoringSomethingThatIsNotADigestIsRefused()
    {
        // The one mistake this type exists to prevent is somebody passing the token itself.
        var (token, _) = Invitation.MintToken();

        Assert.Throws<ArgumentException>(() => Invitation.Issue(
            Guid.CreateVersion7(),
            "someone@example.com",
            Guid.CreateVersion7(),
            [MemberRole.Requester],
            token,
            now: Now));
    }

    [Fact]
    public void ASpentInvitationSaysSoRatherThanSayingItExpired()
    {
        var invitation = Issue();

        Assert.Equal(InvitationRejection.None, invitation.TryConsume(Guid.CreateVersion7(), Now));

        // A month later, the same link. Both "used" and "expired" are true; only one of them tells
        // the person that their account already exists.
        var later = Now.AddDays(30);
        Assert.True(invitation.IsExpired(later));
        Assert.Equal(InvitationRejection.AlreadyUsed, invitation.TryConsume(Guid.CreateVersion7(), later));
    }

    [Fact]
    public void RevokingLeavesASpentInvitationAloneAndIsIdempotent()
    {
        var spent = Issue();
        _ = spent.TryConsume(Guid.CreateVersion7(), Now);
        spent.Revoke(Now);

        Assert.Null(spent.RevokedAt);

        var outstanding = Issue();
        outstanding.Revoke(Now);
        outstanding.Revoke(Now.AddDays(1));

        Assert.Equal(Now, outstanding.RevokedAt);
        Assert.False(outstanding.IsOutstanding(Now));
        Assert.Equal(InvitationRejection.Revoked, outstanding.TryConsume(Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void AnInvitationMustNameARole()
    {
        var (_, hash) = Invitation.MintToken();

        Assert.Throws<ArgumentException>(() => Invitation.Issue(
            Guid.CreateVersion7(),
            "someone@example.com",
            Guid.CreateVersion7(),
            [],
            hash,
            now: Now));
    }

    private static Invitation Issue()
    {
        var (_, hash) = Invitation.MintToken();

        return Invitation.Issue(
            Guid.CreateVersion7(),
            "someone@example.com",
            Guid.CreateVersion7(),
            [MemberRole.Requester, MemberRole.Requester],
            hash,
            now: Now);
    }
}
