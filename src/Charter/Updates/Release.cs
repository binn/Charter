namespace Charter.Updates;

/// <summary>One entry from the Charter repository's public releases list (section 28).</summary>
/// <param name="Tag">The <c>tag_name</c>, as published.</param>
/// <param name="Version">The version parsed out of <paramref name="Tag"/>.</param>
/// <param name="Title">The release title, which carries the section 28 markers.</param>
/// <param name="Url">The release page, linked from the banner.</param>
/// <param name="Notes">The release body, rendered inline after sanitisation by the UI.</param>
/// <param name="IsPrerelease">Whether GitHub marks it a prerelease.</param>
/// <param name="PublishedAt">When it was published, or <see langword="null"/> if unpublished.</param>
public sealed record Release(
    string Tag,
    ReleaseVersion Version,
    string Title,
    string Url,
    string Notes,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt)
{
    /// <summary>
    /// The title prefix that makes a release a security release (section 28, <c>CHANGELOG.md</c>).
    /// </summary>
    /// <remarks>
    /// Section 28 asks for a marker in the release rather than a heuristic over its prose, because the
    /// difference between "there is an update" and "there is a fix for something that can be
    /// exploited" decides whether the notice can be dismissed.
    /// </remarks>
    public const string SecurityMarker = "[SECURITY]";

    /// <summary>
    /// The marker that says an upgrade carries schema migrations, so a backup is warranted.
    /// </summary>
    /// <remarks>
    /// Section 28 requires the warning; <c>CHANGELOG.md</c> and <c>docs/upgrading.md</c> fix the
    /// convention that produces it. Recognised in the title or anywhere in the body, so a release note
    /// that mentions it in a callout counts without the title having to carry two markers.
    /// </remarks>
    public const string MigrationsMarker = "[MIGRATIONS]";

    /// <summary>True when this release fixes something exploitable (section 28).</summary>
    public bool IsSecurity => Contains(Title, SecurityMarker);

    /// <summary>True when upgrading to this release applies schema migrations (sections 15, 28).</summary>
    public bool IncludesMigrations
        => Contains(Title, MigrationsMarker) || Contains(Notes, MigrationsMarker);

    private static bool Contains(string? text, string marker)
        => text is not null && text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
