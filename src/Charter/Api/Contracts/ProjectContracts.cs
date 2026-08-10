namespace Charter.Api.Contracts;

/// <summary>A guided intake field on a template (section 8, <c>.charter/templates/</c>).</summary>
public sealed record RequestTemplateFieldResponse
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public string? Placeholder { get; init; }

    public required bool Required { get; init; }

    public required bool Multiline { get; init; }
}

/// <summary>
/// Section 8: "a requester picking 'change some text' instead of free-typing skips half the
/// refinement round-trips."
/// </summary>
public sealed record RequestTemplateResponse
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>One line, requester-facing: what this template is for.</summary>
    public required string Description { get; init; }

    /// <summary>Stable key the client maps to an icon; unknown values fall back to a generic mark.</summary>
    public string? Icon { get; init; }

    /// <summary>Seed text for the intake box, or a scaffold with <c>{{field}}</c> placeholders.</summary>
    public required string Prompt { get; init; }

    public IReadOnlyList<RequestTemplateFieldResponse>? Fields { get; init; }
}

/// <summary>
/// The requester-facing projection of a <see cref="Charter.Domain.Repo"/>.
/// </summary>
/// <remarks>
/// Section 7.1: a requester never sees a repo name, branch, diff or token count, so
/// <see cref="Name"/> is the operator's display name for the project and never <c>owner/repo</c>. A
/// repo that has not passed its smoke test (section 9), or that the viewer is not scoped to
/// (section 7.3, deny by default), is not in the list at all — absence is the enforcement, not a
/// disabled state.
/// </remarks>
public sealed record ProjectResponse
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Section 8 <c>primer.md</c>: "how this app is put together", for requesters.</summary>
    public string? PrimerMd { get; init; }

    public required IReadOnlyList<RequestTemplateResponse> Templates { get; init; }
}
