using Charter.Api.Contracts;
using Charter.Api.Viewer;
using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 12: <em>"defaults by role (requester → 1, engineer → 3), then persisted per user as a
/// preference"</em>.
/// </summary>
/// <remarks>
/// The subtlety worth pinning is the difference between <em>unchosen</em> and <em>chose Simple</em>.
/// Writing <c>simple</c> into the column at account creation would make an engineer who deliberately
/// works in pane 1 indistinguishable from one who has never touched the setting — and the role
/// default would then bump them back to pane 3 on every load, forever.
/// </remarks>
public class ApiPanePreferenceTests
{
    [Fact]
    public void ARequesterWhoHasNeverChosenLandsOnSimple()
        => Assert.Equal(ApiPanePreference.Simple, ViewerService.PaneFor(Member(MemberRole.Requester), Unchosen()));

    [Fact]
    public void AnEngineerWhoHasNeverChosenLandsOnDeveloper()
        => Assert.Equal(ApiPanePreference.Developer, ViewerService.PaneFor(Member(MemberRole.Engineer), Unchosen()));

    [Fact]
    public void AnAdminLandsOnDeveloperToo()
        => Assert.Equal(ApiPanePreference.Developer, ViewerService.PaneFor(Member(MemberRole.Admin), Unchosen()));

    [Fact]
    public void AnApproverIsNotAnEngineerAndLandsOnSimple()
    {
        // Section 7.5: the approver gates spend, not code. Pane 3 is repository read (section 7.4),
        // which they do not have by holding that role.
        Assert.Equal(ApiPanePreference.Simple, ViewerService.PaneFor(Member(MemberRole.Approver), Unchosen()));
    }

    [Fact]
    public void RolesAreAdditiveSoOneEngineerRoleIsEnough()
    {
        var both = Member(MemberRole.Requester, MemberRole.Engineer);

        Assert.Equal(ApiPanePreference.Developer, ViewerService.PaneFor(both, Unchosen()));
    }

    [Fact]
    public void AChoiceAlwaysWinsOverTheRoleDefault()
    {
        // The engineer who works in pane 1 on purpose. Seeding by role must be a first-read default,
        // never a correction applied on every load.
        var engineer = Member(MemberRole.Engineer);
        var chose = Unchosen() with { Pane = ApiPanePreference.Simple, PaneIsExplicit = true };

        Assert.Equal(ApiPanePreference.Simple, ViewerService.PaneFor(engineer, chose));

        var requester = Member(MemberRole.Requester);
        var chosePane3 = Unchosen() with { Pane = ApiPanePreference.Developer, PaneIsExplicit = true };

        Assert.Equal(ApiPanePreference.Developer, ViewerService.PaneFor(requester, chosePane3));
    }

    [Fact]
    public void PatchingSomethingElseStillResolvesThePaneByRole()
    {
        // `PATCH /api/me/preferences` returns the full resolved set, and the endpoint resolves it
        // through the same call `GET /api/me` uses. An engineer who changes only their theme must not
        // get `simple` back and have the client store it as their pane.
        var engineer = Member(MemberRole.Engineer);
        var afterThemePatch = Unchosen() with { Theme = ApiThemePreference.Dark };

        Assert.Equal(ApiPanePreference.Developer, ViewerService.PaneFor(engineer, afterThemePatch));
    }

    private static ViewerPreferencesRecord Unchosen() => new()
    {
        Theme = ApiThemePreference.System,

        // What the store resolves an unset column to, together with the flag that says so.
        Pane = ApiPanePreference.Simple,
        PaneIsExplicit = false,
        TeachingLevel = ApiTeachingLevel.ExplainEverything,
    };

    private static MemberSnapshot Member(params MemberRole[] roles) => new()
    {
        MemberId = Guid.CreateVersion7(),
        OrgId = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        Roles = roles,
    };
}
