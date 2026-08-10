namespace Charter.Domain;

/// <summary>
/// How much explanation a person wants, named for what the reader wants rather than what they lack
/// (section 13).
/// </summary>
public enum TeachingLevel
{
    /// <summary>Assumes no vocabulary; every term defined on first use.</summary>
    ExplainEverything,

    /// <summary>Knows what a database and a deploy are; wants the reasoning.</summary>
    SkipTheBasics,

    /// <summary>Trade-offs and alternatives only, no mechanics.</summary>
    JustTheDecisions,
}

/// <summary>
/// The three theme settings the shell offers (section 12).
/// </summary>
/// <remarks>
/// <see cref="System"/> is both the default and the "never chose" value: it means <em>follow the
/// operating system</em>, which is what an untouched preference should do anyway. There is no
/// browser storage in this app (section 3.1), so this column is the only place the choice exists.
/// </remarks>
public enum ThemePreference
{
    System,

    Light,

    Dark,
}

/// <summary>
/// Which of the three panes a person lands in, named for the user rather than for the machinery
/// (section 12): Simple / Detailed / Developer.
/// </summary>
/// <remarks>
/// A preference, not a permission. Section 7.4 decides whether panes 2 and 3 are <em>sent</em> at
/// all; this only decides which one opens first for somebody who may see them.
/// </remarks>
public enum PanePreference
{
    Simple,

    Detailed,

    Developer,
}

/// <summary>A human. Users are global; membership of an organisation is <see cref="Member"/>.</summary>
public sealed class User
{
    private User()
    {
    }

    private User(Guid id, string email, string displayName, TeachingLevel teachingLevel, DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        TeachingLevel = teachingLevel;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Stored lower-cased so the unique index is a real uniqueness guarantee.</summary>
    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public TeachingLevel TeachingLevel { get; private set; }

    /// <summary>Section 12. Defaults to following the operating system.</summary>
    public ThemePreference Theme { get; private set; }

    /// <summary>
    /// Section 12: <em>"defaults by role (requester → 1, engineer → 3), then persisted per user as a
    /// preference"</em>.
    /// </summary>
    /// <remarks>
    /// Null means nobody has chosen yet, which is what lets the role-based default apply. Storing
    /// <see cref="PanePreference.Simple"/> at creation time instead would be indistinguishable from
    /// an engineer who deliberately picked pane 1, and the default would then be un-appliable
    /// forever.
    /// </remarks>
    public PanePreference? Pane { get; private set; }

    /// <summary>Section 30.4. Null until the three requester onboarding screens are done.</summary>
    public DateTimeOffset? RequesterOnboardingCompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(
        string email,
        string displayName,
        TeachingLevel teachingLevel = TeachingLevel.ExplainEverything,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new User(
            id ?? Guid.CreateVersion7(),
            NormalizeEmail(email),
            displayName.Trim(),
            teachingLevel,
            DomainTime.Resolve(now));
    }

    public void ChangeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Email = NormalizeEmail(email);
    }

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }

    /// <summary>Section 13: the calibration is asked once and changeable later.</summary>
    public void SetTeachingLevel(TeachingLevel level) => TeachingLevel = level;

    /// <summary>Section 12: the theme is a stored preference, not a browser setting.</summary>
    public void SetTheme(ThemePreference theme) => Theme = theme;

    /// <summary>Section 12: the pane a person lands in, once they have chosen one.</summary>
    public void SetPane(PanePreference pane) => Pane = pane;

    /// <summary>
    /// Section 30.4. Idempotent: completing twice keeps the first timestamp, because the second call
    /// is a re-render of a screen somebody already finished rather than a new event.
    /// </summary>
    public void CompleteRequesterOnboarding(DateTimeOffset? now = null)
        => RequesterOnboardingCompletedAt ??= DomainTime.Resolve(now);

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
