namespace Charter.Api.Contracts;

/// <summary>The organisation a viewer is acting in.</summary>
public sealed record OrganizationResponse
{
    /// <summary>Opaque to the client.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
}

/// <summary>
/// Preferences live server-side against the user record. There is no browser storage in this app
/// (section 3.1), so this object is the only place a preference exists on the client.
/// </summary>
public sealed record UserPreferencesResponse
{
    public required ApiThemePreference Theme { get; init; }

    public required ApiPanePreference Pane { get; init; }

    public required ApiTeachingLevel TeachingLevel { get; init; }
}

/// <summary>
/// Server-computed capabilities.
/// </summary>
/// <remarks>
/// Section 7.2. These drive <em>navigation and affordances only</em> — which links exist, whether a
/// button is offered. They never gate the rendering of data, because data a viewer may not see is
/// not in the payload at all (section 7.4). Keeping the role arithmetic on the server is what stops
/// the client drifting from the authorisation code path.
/// </remarks>
public sealed record ViewerCapabilitiesResponse
{
    public required bool CanFileRequests { get; init; }

    public required bool CanApproveSpend { get; init; }

    /// <summary>Repo read access. Governs whether the server <em>sends</em> transcripts and details.</summary>
    public required bool CanReadRepos { get; init; }

    public required bool CanAdminister { get; init; }
}

/// <summary><c>GET /api/me</c>.</summary>
public sealed record ViewerResponse
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Email { get; init; }

    public required OrganizationResponse Organization { get; init; }

    public required IReadOnlyList<ApiRole> Roles { get; init; }

    public required ViewerCapabilitiesResponse Capabilities { get; init; }

    public required UserPreferencesResponse Preferences { get; init; }

    /// <summary>Section 30.4. Absent until the requester has completed the three onboarding screens.</summary>
    public DateTimeOffset? RequesterOnboardingCompletedAt { get; init; }
}

/// <summary><c>PATCH /api/me/preferences</c>. Every member is optional: this is a partial.</summary>
public sealed record UpdatePreferencesBody
{
    public ApiThemePreference? Theme { get; init; }

    public ApiPanePreference? Pane { get; init; }

    public ApiTeachingLevel? TeachingLevel { get; init; }
}
