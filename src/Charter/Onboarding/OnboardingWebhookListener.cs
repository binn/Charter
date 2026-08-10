using Charter.Data;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Onboarding;

/// <summary>
/// Reacts to the GitHub events onboarding cares about.
/// </summary>
/// <remarks>
/// <para>
/// Three things, and deliberately only three.
/// </para>
/// <para>
/// An <c>installation</c> or <c>installation_repositories</c> delivery tells Charter which
/// repositories the App can now reach. It does <em>not</em> connect them: section 26.10 and section
/// 7.3 both say a repository becomes usable through a deliberate, attributable act, and "an admin
/// ticked a box in GitHub" is not that act. What this does is log the reachable set so the connect
/// wizard can offer it.
/// </para>
/// <para>
/// A <c>push</c> to a repository's base branch means its committed guardrails may have changed, so
/// the stored snapshot is refreshed. This is the mechanism that makes the scope-config pull request
/// take effect when a human merges it, and it is why Charter never needed a "reload config" button.
/// </para>
/// <para>
/// A <c>check_suite</c> conclusion is the "checks pass" leg of the smoke test. It is recorded, not
/// acted on: deciding that a smoke test passed needs all six integration points, and only the
/// execution plane sees the rest.
/// </para>
/// </remarks>
public sealed class OnboardingWebhookListener : IGitHubWebhookListener
{
    private readonly CharterDbContext _database;
    private readonly OnboardingService _onboarding;
    private readonly CharterFolderCache _cache;
    private readonly ILogger<OnboardingWebhookListener> _logger;

    public OnboardingWebhookListener(
        CharterDbContext database,
        OnboardingService onboarding,
        CharterFolderCache cache,
        ILogger<OnboardingWebhookListener> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _onboarding = onboarding;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnDeliveryAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        switch (delivery.Type)
        {
            case GitHubWebhookEventType.Installation:
            case GitHubWebhookEventType.InstallationRepositories:
                await OnInstallationAsync(delivery, cancellationToken);
                break;

            case GitHubWebhookEventType.Push:
                await OnPushAsync(delivery, cancellationToken);
                break;

            case GitHubWebhookEventType.CheckSuite:
                OnCheckSuite(delivery);
                break;

            default:
                break;
        }
    }

    private async Task OnInstallationAsync(GitHubWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.InstallationId is not { } installationId)
        {
            return;
        }

        // "deleted", "suspend" and "removed" take repositories away. A repository Charter can no
        // longer reach must stop being requestable immediately — leaving it ready would let a
        // requester file work that can never run.
        var removing = delivery.Action is "deleted" or "suspend" or "removed";

        if (!removing)
        {
            _logger.LogInformation(
                "GitHub App installation {InstallationId} now reaches {Count} repositor(ies); none are "
                + "connected until somebody connects them",
                installationId,
                delivery.RepositoryFullNames.Count);

            return;
        }

        var affected = delivery.RepositoryFullNames.Count > 0
            ? await _database.Repos
                .Where(repo => repo.GithubInstallationId == installationId
                               && delivery.RepositoryFullNames.Contains(repo.FullName))
                .ToListAsync(cancellationToken)
            : await _database.Repos
                .Where(repo => repo.GithubInstallationId == installationId)
                .ToListAsync(cancellationToken);

        foreach (var repo in affected)
        {
            await _onboarding.SetEnabledAsync(repo.Id, enabled: false, cancellationToken: cancellationToken);
            _cache.Evict(repo.FullName);

            _logger.LogWarning(
                "{Repository} was disabled: the GitHub App installation no longer reaches it",
                repo.FullName);
        }
    }

    private async Task OnPushAsync(GitHubWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.RepositoryFullName is not { Length: > 0 } fullName || delivery.Branch is not { } branch)
        {
            return;
        }

        var repo = await _database.Repos.FirstOrDefaultAsync(
            candidate => candidate.FullName == fullName,
            cancellationToken);

        if (repo is null || !string.Equals(repo.BaseBranch, branch, StringComparison.Ordinal))
        {
            return;
        }

        // The commit moved, so anything cached against the old head is now the wrong answer for
        // "what does the base branch say".
        _cache.Evict(repo.FullName);

        var warnings = await _onboarding.RefreshSnapshotAsync(repo, cancellationToken);

        foreach (var warning in warnings)
        {
            _logger.LogWarning("{Repository}: {Warning}", repo.FullName, warning);
        }
    }

    private void OnCheckSuite(GitHubWebhookDelivery delivery)
    {
        if (delivery.Action is not "completed")
        {
            return;
        }

        _logger.LogInformation(
            "Checks for {Repository}@{HeadSha} concluded {Conclusion}",
            delivery.RepositoryFullName ?? "-",
            delivery.HeadSha ?? "-",
            delivery.CheckSuiteConclusion ?? "-");
    }
}
