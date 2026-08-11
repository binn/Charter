namespace Charter.Api.Contracts;

/// <summary>An outstanding invitation, as the admin members screen shows it (section 30.2).</summary>
/// <remarks>
/// There is no token on this type and no endpoint that reads one back. The database holds a digest,
/// not the token (see <c>Invitation</c>), so a lost invitation is reissued rather than looked up —
/// and a list endpoint that could hand back a live account-creating credential would undo that.
/// </remarks>
public sealed record InvitationResponse
{
    public required string Id { get; init; }

    public required string Email { get; init; }

    /// <summary>What they are being invited as (section 7.1). Additive, and at least one.</summary>
    public required IReadOnlyList<ApiRole> Roles { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary><c>GET /api/invitations</c>.</summary>
public sealed record InvitationsResponse
{
    /// <summary>Outstanding only: nothing spent, revoked or expired.</summary>
    public required IReadOnlyList<InvitationResponse> Invitations { get; init; }

    /// <summary>
    /// True when Charter can email an invitation. False means every invite comes back with a link
    /// for the admin to pass on themselves (change spec 001 part C.1).
    /// </summary>
    public required bool EmailEnabled { get; init; }
}

/// <summary><c>POST /api/invitations</c>.</summary>
public sealed record InviteMemberBody
{
    public string? Email { get; init; }

    /// <summary>Defaults to <see cref="ApiRole.Requester"/> — the least a member can be.</summary>
    public IReadOnlyList<ApiRole>? Roles { get; init; }
}

/// <summary>
/// The result of minting a one-time link: whether it was emailed, and the link when the person
/// looking at the screen is entitled to see it.
/// </summary>
/// <remarks>
/// <see cref="Link"/> is present only for an administrator acting on somebody else's account, and
/// only when email did not carry it. It is <em>absent</em> rather than null otherwise (section 7.4),
/// so the client's test is <c>'link' in response</c> and there is no null to mistake for a value.
/// </remarks>
public sealed record OneTimeLinkResponse
{
    /// <summary>True when Charter delivered it by email.</summary>
    public required bool Emailed { get; init; }

    /// <summary>One sentence for whoever is looking at the screen (section 11).</summary>
    public required string Message { get; init; }

    /// <summary>The one-time link, when the caller may see it. Absent otherwise.</summary>
    public string? Link { get; init; }
}

/// <summary>The response to inviting somebody: the row, plus how the link got to them.</summary>
public sealed record InvitationIssuedResponse
{
    public required InvitationResponse Invitation { get; init; }

    public required OneTimeLinkResponse Delivery { get; init; }
}

/// <summary><c>POST /api/password-resets</c>: an admin resetting somebody else's password.</summary>
public sealed record AdminPasswordResetBody
{
    /// <summary>The account to mint a link for.</summary>
    public string? Email { get; init; }
}
