using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Viewer;

/// <summary>
/// Builds <c>GET /api/me</c>.
/// </summary>
/// <remarks>
/// Section 7.2: the capability flags are computed here, on the server, by asking the same policies
/// the endpoints ask. They exist so the client knows which links to draw — they never gate the
/// rendering of data, because data a viewer may not see is not in the payload (section 7.4). Keeping
/// the arithmetic here is what stops the client's idea of a role drifting from the authoriser's.
/// </remarks>
public sealed class ViewerService
{
    private readonly CharterDbContext database;
    private readonly IViewerPreferencesStore preferences;
    private readonly RequestQueryService requests;

    public ViewerService(
        CharterDbContext database,
        IViewerPreferencesStore preferences,
        RequestQueryService requests)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(requests);

        this.database = database;
        this.preferences = preferences;
        this.requests = requests;
    }

    /// <summary>The signed-in viewer, or <c>null</c> when the user or organisation row has gone.</summary>
    public async Task<ViewerResponse?> DescribeAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == member.UserId, cancellationToken);

        var organization = await database.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == member.OrgId, cancellationToken);

        if (user is null || organization is null)
        {
            return null;
        }

        var resolved = await preferences.GetAsync(member.UserId, cancellationToken);

        return new ViewerResponse
        {
            Id = user.Id.ToString(),
            DisplayName = user.DisplayName,
            Email = user.Email,
            Organization = new OrganizationResponse
            {
                Id = organization.Id.ToString(),
                Name = organization.Name,
            },
            Roles = [.. member.Roles.Select(role => role.ToApi())],
            Capabilities = await CapabilitiesAsync(member, cancellationToken),
            Preferences = new UserPreferencesResponse
            {
                Theme = resolved.Theme,
                Pane = PaneFor(member, resolved),
                TeachingLevel = resolved.TeachingLevel,
            },
            RequesterOnboardingCompletedAt = resolved.RequesterOnboardingCompletedAt,
        };
    }

    /// <summary>
    /// Section 12: <em>"defaults by role (requester → 1, engineer → 3), then persisted per user as a
    /// preference"</em>.
    /// </summary>
    /// <remarks>
    /// The stored choice always wins. The role only decides what somebody who has never chosen sees
    /// first, which is why the store keeps "unchosen" distinguishable from "chose Simple" — an
    /// engineer who deliberately works in pane 1 must not be bumped back to pane 3 on every load.
    /// </remarks>
    public static ApiPanePreference PaneFor(MemberSnapshot member, ViewerPreferencesRecord preferences)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences.PaneIsExplicit)
        {
            return preferences.Pane;
        }

        return member.HasRole(MemberRole.Engineer) || member.HasRole(MemberRole.Admin)
            ? ApiPanePreference.Developer
            : preferences.Pane;
    }

    /// <summary>
    /// The capability flags, each answered by the policy that actually owns the question.
    /// </summary>
    /// <remarks>
    /// <see cref="ViewerCapabilitiesResponse.CanFileRequests"/> is deliberately "is there anywhere to
    /// file", not "does this person hold the requester role": section 7.3 is deny-by-default per
    /// repository, so somebody holding the role with no scope row has nowhere to file and should not
    /// be shown the button.
    /// </remarks>
    public async Task<ViewerCapabilitiesResponse> CapabilitiesAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        // Section 7.4: repository read is one question with one answer for the whole organisation, so
        // it is asked of RepoAccessPolicy rather than restated here as "holds the engineer role".
        var probe = new RepoSnapshot
        {
            RepoId = Guid.Empty,
            OrgId = member.OrgId,
            Status = RepoStatus.Ready,
        };

        var projects = await requests.ListProjectsAsync(member, cancellationToken);

        return new ViewerCapabilitiesResponse
        {
            CanFileRequests = projects.Count > 0,

            // Section 7.5, mirrored from SpecApprovalPolicy: the approver holds the spend gate, and an
            // admin who should also approve holds the approver role too, because roles are additive.
            CanApproveSpend = member.HasRole(MemberRole.Approver),
            CanReadRepos = RepoAccessPolicy.CanReadRepository(member, probe).IsAllowed,
            CanAdminister = member.HasRole(MemberRole.Admin),
        };
    }

    /// <summary>Applies a preference patch and returns the full resolved set.</summary>
    public Task<ViewerPreferencesRecord> UpdatePreferencesAsync(
        MemberSnapshot member,
        UpdatePreferencesBody patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(patch);

        return preferences.UpdateAsync(member.UserId, patch, cancellationToken);
    }

    /// <summary>Section 30.4: the three onboarding screens are done.</summary>
    public Task<ViewerPreferencesRecord> CompleteRequesterOnboardingAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        return preferences.CompleteRequesterOnboardingAsync(member.UserId, cancellationToken);
    }
}
