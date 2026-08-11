namespace Charter.Api.Contracts;

/// <summary>
/// One member of the organisation, as the admin members screen shows them (section 7.1).
/// </summary>
/// <remarks>
/// Administrators only. Section 7.1 gives members and roles to the admin column, and this type
/// carries an email address, so nothing below admin reaches the endpoint that produces it.
/// </remarks>
public sealed record MemberResponse
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Email { get; init; }

    /// <summary>Additive, and always at least one (section 7.1).</summary>
    public required IReadOnlyList<ApiRole> Roles { get; init; }

    /// <summary>Section 26.10: repo creation is a capability rather than a role.</summary>
    public required bool CanCreateRepo { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }

    /// <summary>True for the administrator reading the screen, so it can say so rather than imply it.</summary>
    public required bool IsYou { get; init; }
}

/// <summary><c>GET /api/members</c>.</summary>
public sealed record MembersResponse
{
    public required IReadOnlyList<MemberResponse> Members { get; init; }
}

/// <summary>
/// <c>POST /api/members/{id}/roles</c>: add or remove one role.
/// </summary>
/// <remarks>
/// One role per call rather than a whole set. A set replaces state the caller may have read
/// minutes ago; a single verb says exactly what the administrator meant, and it is what the audit
/// log records (<c>member.role.granted</c>, <c>member.role.revoked</c>).
/// </remarks>
public sealed record SetMemberRoleBody
{
    public ApiRole? Role { get; init; }

    /// <summary>True adds the role, false removes it.</summary>
    public bool Granted { get; init; }
}

/// <summary>
/// One entry of the audit log (section 7.3, guardrail 5; section 7.1 gives it to admins).
/// </summary>
/// <remarks>
/// <para>
/// The dotted verb is carried alongside a plain-English sentence rather than instead of it. The verb
/// is what an operator greps for and what the code writes; the sentence is what makes the screen
/// readable by the administrator who has to answer "who made this repository requestable".
/// </para>
/// <para>
/// <see cref="Details"/> is a deliberately small subset of the entry's metadata: short values only,
/// a handful of keys. The audit log holds no secrets by design (section 19), but it does hold
/// structured payloads that would turn this screen into a wall of JSON.
/// </para>
/// </remarks>
public sealed record AuditEntryResponse
{
    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The dotted verb, such as <c>repo.scope.granted</c>.</summary>
    public required string Action { get; init; }

    /// <summary>One sentence, for a human reading the list.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Who did it. Absent for the few things Charter does itself, such as lease expiry — and that
    /// absence is the point: section 7.3 says the agent never acts on its own initiative.
    /// </summary>
    public string? ActorName { get; init; }

    public string? ActorEmail { get; init; }

    public required string TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>Short metadata, when there is any. Absent otherwise.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary><c>GET /api/audit</c>.</summary>
public sealed record AuditLogResponse
{
    /// <summary>Newest first.</summary>
    public required IReadOnlyList<AuditEntryResponse> Entries { get; init; }

    /// <summary>True when older entries exist beyond the page returned.</summary>
    public required bool HasMore { get; init; }
}
