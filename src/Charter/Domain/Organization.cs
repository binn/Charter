namespace Charter.Domain;

/// <summary>How an organisation is operated. Section 7.2: personal mode is not a mode.</summary>
public enum OrganizationMode
{
    /// <summary>One member holding every role, with approval gates auto-satisfied by policy.</summary>
    Personal,

    /// <summary>Several members, roles granted deliberately.</summary>
    Organization,
}

/// <summary>The tenant every other row hangs from (section 5).</summary>
/// <remarks>
/// Personal mode is an <see cref="Organization"/> with one <see cref="Member"/> holding all roles.
/// Same tables, same authorisation path; only the seeded defaults differ (section 7.2).
/// </remarks>
public sealed class Organization
{
    private Organization()
    {
    }

    private Organization(Guid id, string name, OrganizationMode mode, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Mode = mode;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public OrganizationMode Mode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Organization Create(
        string name,
        OrganizationMode mode = OrganizationMode.Personal,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Organization(id ?? Guid.CreateVersion7(), name.Trim(), mode, DomainTime.Resolve(now));
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>
    /// Promotes a personal instance to an organisation. Section 7.2: inviting a second user is the
    /// only thing that changes, and it must require no migration.
    /// </summary>
    public void PromoteToOrganization() => Mode = OrganizationMode.Organization;
}
