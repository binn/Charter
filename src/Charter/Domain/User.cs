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

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
