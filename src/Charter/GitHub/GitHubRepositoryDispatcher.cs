using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Charter.Data;
using Charter.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.GitHub;

/// <summary>
/// The two seams the execution plane declares, implemented over the GitHub App installation token.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IGitHubRepositoryDispatcher"/> and <see cref="IRunnerCredentialBroker"/> are both
/// declared in <c>Charter.Runners</c> and both say they are implemented here, so the execution plane
/// keeps no compile-time dependency on the GitHub client and a dispatch test needs no network.
/// </para>
/// <para>
/// The installation id is resolved from the connected <see cref="Charter.Domain.Repo"/> row rather
/// than passed in. A caller that could name an arbitrary installation could dispatch into a
/// repository this instance was never connected to; looking it up means an unconnected repository has
/// no token to be had.
/// </para>
/// <para>
/// A singleton, because the runners that consume it are singletons — so the scoped
/// <see cref="CharterDbContext"/> it needs for that lookup is taken from a scope created per call
/// rather than captured.
/// </para>
/// </remarks>
public sealed class GitHubRepositoryDispatcher : IGitHubRepositoryDispatcher, IRunnerCredentialBroker
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAppTokenProvider _tokens;
    private readonly GitHubOptions _options;

    public GitHubRepositoryDispatcher(
        IServiceScopeFactory scopes,
        IHttpClientFactory httpClientFactory,
        IGitHubAppTokenProvider tokens,
        GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);

        _scopes = scopes;
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _options = options;
    }

    /// <inheritdoc />
    public async Task RepositoryDispatchAsync(
        string repoFullName,
        string eventType,
        string clientPayloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var repository = await ResolveAsync(repoFullName, cancellationToken);

        var payload = string.IsNullOrWhiteSpace(clientPayloadJson) ? "{}" : clientPayloadJson;
        var body = $$"""{"event_type":{{JsonSerializer.Serialize(eventType)}},"client_payload":{{payload}}}""";

        using var response = await SendAsync(
            repository,
            HttpMethod.Post,
            $"repos/{repository.Owner}/{repository.Name}/dispatches",
            body,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(
                $"GitHub answered {(int)response.StatusCode} to repository_dispatch on {repoFullName}.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelWorkflowRunAsync(
        string repoFullName,
        long runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);

        var repository = await ResolveAsync(repoFullName, cancellationToken);

        using var response = await SendAsync(
            repository,
            HttpMethod.Post,
            $"repos/{repository.Owner}/{repository.Name}/actions/runs/{runId}/cancel",
            body: null,
            cancellationToken);

        // 409 is GitHub's "that run has already finished", and 404 its "there is no such run".
        // Neither is a failure worth propagating: the caller asked for it to stop, and it has.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }

        return response.IsSuccessStatusCode
            ? true
            : throw new GitHubApiException(
                $"GitHub answered {(int)response.StatusCode} cancelling run {runId} in {repoFullName}.");
    }

    /// <summary>
    /// Mints the credential a sandbox holds for one job (sections 7.4, 33.5).
    /// </summary>
    /// <remarks>
    /// Short TTL, one repository, and no renewal path — the token is handed over as a value, and the
    /// provider that could mint another stays in the control plane. The model half of
    /// <see cref="RunnerCredentials"/> is left null here: model credentials are section 20b's, and
    /// resolving one belongs to the credential resolver rather than to the GitHub client.
    /// </remarks>
    public async Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);

        var repository = await ResolveAsync(repoFullName, cancellationToken);

        var token = await _tokens.GetInstallationTokenAsync(
            repository,
            GitHubTokenScope.Contribute,
            cancellationToken);

        return new RunnerCredentials(GitHubRunnerCredential.From(token).Token.Reveal(), null);
    }

    private async Task<GitHubRepository> ResolveAsync(string repoFullName, CancellationToken cancellationToken)
    {
        var name = repoFullName.Trim();

        await using var scope = _scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var installationId = await database.Repos
            .Where(repo => repo.FullName == name)
            .Select(repo => (long?)repo.GithubInstallationId)
            .FirstOrDefaultAsync(cancellationToken);

        return installationId is { } id
            ? GitHubRepository.Parse(name, id)
            : throw new GitHubApiException(
                $"{name} is not a repository connected to this Charter instance, so no installation "
                + "token can be minted for it.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        GitHubRepository repository,
        HttpMethod method,
        string path,
        string? body,
        CancellationToken cancellationToken)
    {
        // Actions dispatch needs a write token: `contents: write` is what GitHub requires for
        // repository_dispatch, and the workflow-cancel call rides the same scope.
        var token = await _tokens.GetInstallationTokenAsync(
            repository,
            GitHubTokenScope.Contribute,
            cancellationToken);

        using var request = new HttpRequestMessage(method, new Uri(_options.ApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token.Reveal());
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var client = _httpClientFactory.CreateClient(GitHubAppTokenProvider.HttpClientName);

        return await client.SendAsync(request, cancellationToken);
    }
}
