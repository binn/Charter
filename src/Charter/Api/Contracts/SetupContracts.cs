namespace Charter.Api.Contracts;

/// <summary>One row of the section 30.2 admin checklist.</summary>
public sealed record SetupTaskResponse
{
    public required ApiSetupTaskId Id { get; init; }

    public required string Title { get; init; }

    /// <summary>One line saying why this one matters. Not instructions — the destination has those.</summary>
    public required string Description { get; init; }

    public required bool Done { get; init; }

    /// <summary>Where the task actually gets done: an in-app route, or an external install URL.</summary>
    public required string Href { get; init; }

    public bool? External { get; init; }

    /// <summary>
    /// Not a lock — an explanation.
    /// </summary>
    /// <remarks>
    /// <em>"Connect GitHub first"</em> is information. The checklist never disables a row, because
    /// section 30.2's whole point is that somebody can leave and come back in any order they like.
    /// </remarks>
    public ApiSetupTaskId? BlockedBy { get; init; }

    /// <summary>What is configured, once done: <em>"3 repositories"</em>, <em>"Anthropic"</em>. Proof, not a tick.</summary>
    public string? DoneSummary { get; init; }
}

/// <summary>
/// Section 30.2. A <strong>persistent dashboard checklist, not a modal wizard</strong> — "modal
/// wizards trap people who need to go find a token". Resumable, showing progress, dismissible once
/// complete.
/// </summary>
/// <remarks>
/// <c>GET /api/setup/checklist</c> answers <c>null</c> rather than 403 for anyone who is not an
/// admin: a requester's dashboard has no checklist on it, and that is not an error state the page
/// should have to render.
/// </remarks>
public sealed record SetupChecklistResponse
{
    public required IReadOnlyList<SetupTaskResponse> Tasks { get; init; }

    /// <summary>Set once dismissed. A server-side fact like every other preference (section 3.1).</summary>
    public DateTimeOffset? DismissedAt { get; init; }
}
