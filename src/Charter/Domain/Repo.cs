namespace Charter.Domain;

/// <summary>The onboarding states of section 9, ending in proof rather than configuration.</summary>
public enum RepoStatus
{
    /// <summary>Connected, nothing looked at yet.</summary>
    Pending,

    /// <summary>Read-only agent run over the repository (section 9.2).</summary>
    Recon,

    /// <summary>Scope confirmation; <c>.charter/config.yml</c> proposed as a pull request.</summary>
    Configuring,

    /// <summary>The canned trivial request that exercises all six integration points at once.</summary>
    SmokeTest,

    /// <summary>Smoke test passed. Only now is the repository visible to requesters.</summary>
    Ready,

    Disabled,
}

/// <summary>A connected GitHub repository (section 5).</summary>
/// <remarks>
/// Section 9: a repo is invisible to requesters until the smoke test passes, and section 7.3 makes
/// repo scope deny-by-default, so a newly connected repo is requestable by nobody.
/// </remarks>
public sealed class Repo
{
    private Repo()
    {
    }

    private Repo(
        Guid id,
        Guid orgId,
        long githubInstallationId,
        string fullName,
        string baseBranch,
        RepoStatus status,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        GithubInstallationId = githubInstallationId;
        FullName = fullName;
        BaseBranch = baseBranch;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public long GithubInstallationId { get; private set; }

    /// <summary><c>owner/name</c>, as GitHub spells it.</summary>
    public string FullName { get; private set; } = string.Empty;

    public string BaseBranch { get; private set; } = string.Empty;

    public RepoStatus Status { get; private set; }

    /// <summary>
    /// The last parsed <c>.charter/config.yml</c> (section 8) as jsonb. A snapshot, not the source of
    /// truth: the committed file in the repository is, because changing a guardrail must require a
    /// pull request.
    /// </summary>
    public string? CharterConfigSnapshot { get; private set; }

    /// <summary>Requester-facing "how this app is put together" (section 8).</summary>
    public string? PrimerMd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Section 9: readiness is earned, and it is what makes the repo requestable at all.</summary>
    public bool IsRequesterVisible => Status == RepoStatus.Ready;

    public static Repo Connect(
        Guid orgId,
        long githubInstallationId,
        string fullName,
        string baseBranch = "main",
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(githubInstallationId);

        return new Repo(
            id ?? Guid.CreateVersion7(),
            orgId,
            githubInstallationId,
            fullName.Trim(),
            baseBranch.Trim(),
            RepoStatus.Pending,
            DomainTime.Resolve(now));
    }

    public void TransitionTo(RepoStatus status, DateTimeOffset? now = null)
    {
        Status = status;
        Touch(now);
    }

    public void RecordConfigSnapshot(string? configJson, DateTimeOffset? now = null)
    {
        CharterConfigSnapshot = configJson;
        Touch(now);
    }

    public void PublishPrimer(string primerMd, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primerMd);
        PrimerMd = primerMd;
        Touch(now);
    }

    public void ChangeBaseBranch(string baseBranch, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        BaseBranch = baseBranch.Trim();
        Touch(now);
    }

    private void Touch(DateTimeOffset? now) => UpdatedAt = DomainTime.Resolve(now);
}
