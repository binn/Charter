using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>
/// Everything Charter does to a repository over the REST API.
/// </summary>
/// <remarks>
/// Reads at a ref, one tree listing, a branch, a commit, and a pull request — which is exactly the
/// set the onboarding flow (section 9) and the scope-config proposal (section 8) need, and
/// deliberately nothing more. There is no merge call anywhere in this interface: section 7.4 gives
/// the merge gate to branch protection and CODEOWNERS, so an interface that cannot express it is one
/// fewer thing to get wrong.
/// </remarks>
public interface IGitHubRepositoryClient
{
    /// <summary>The commit a branch points at, or <see langword="null"/> when there is no such branch.</summary>
    Task<string?> GetBranchHeadShaAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>One file at a ref, or <see langword="null"/> when it does not exist there.</summary>
    Task<GitHubFile?> GetFileAsync(
        GitHubRepository repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>The whole tree at a ref, recursively. Empty when the ref has no tree.</summary>
    Task<IReadOnlyList<GitHubTreeEntry>> ListTreeAsync(
        GitHubRepository repository,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>A blob's decoded text, by object SHA.</summary>
    Task<string> GetBlobTextAsync(
        GitHubRepository repository,
        string sha,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a branch at a commit. Fails if the branch already exists.</summary>
    Task CreateBranchAsync(
        GitHubRepository repository,
        string branch,
        string fromSha,
        CancellationToken cancellationToken = default);

    /// <summary>Writes every file in one commit on <paramref name="branch"/>.</summary>
    Task<GitHubCommitResult> CommitFilesAsync(
        GitHubRepository repository,
        string branch,
        string message,
        IReadOnlyList<GitHubFileEdit> files,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a pull request.</summary>
    Task<GitHubPullRequestResult> OpenPullRequestAsync(
        GitHubRepository repository,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class GitHubRepositoryClient : IGitHubRepositoryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAppTokenProvider _tokens;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubRepositoryClient> _logger;

    public GitHubRepositoryClient(
        IHttpClientFactory httpClientFactory,
        IGitHubAppTokenProvider tokens,
        GitHubOptions options,
        ILogger<GitHubRepositoryClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetBranchHeadShaAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/git/ref/heads/{EscapePath(branch)}",
            cancellationToken);

        if (json is null)
        {
            return null;
        }

        using (json)
        {
            return json.RootElement.TryGetProperty("object", out var target)
                   && target.TryGetProperty("sha", out var sha)
                   && sha.ValueKind == JsonValueKind.String
                ? sha.GetString()
                : null;
        }
    }

    /// <inheritdoc />
    public async Task<GitHubFile?> GetFileAsync(
        GitHubRepository repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/contents/{EscapePath(path)}"
            + $"?ref={Uri.EscapeDataString(reference)}",
            cancellationToken);

        if (json is null)
        {
            return null;
        }

        using (json)
        {
            var root = json.RootElement;

            // A directory comes back as an array. Callers of this method want a file.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var sha = root.TryGetProperty("sha", out var shaElement) && shaElement.ValueKind == JsonValueKind.String
                ? shaElement.GetString()!
                : string.Empty;

            var content = root.TryGetProperty("content", out var contentElement)
                          && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

            return new GitHubFile(path, sha, content is null ? string.Empty : DecodeBase64(content));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubTreeEntry>> ListTreeAsync(
        GitHubRepository repository,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/git/trees/{EscapePath(reference)}?recursive=1",
            cancellationToken);

        if (json is null)
        {
            return [];
        }

        using (json)
        {
            if (!json.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            if (json.RootElement.TryGetProperty("truncated", out var truncated)
                && truncated.ValueKind == JsonValueKind.True)
            {
                // Worth knowing about: a truncated tree means a `.charter/` file could be missing
                // from the listing, and silently loading a partial guardrail set is not acceptable.
                _logger.LogWarning(
                    "GitHub truncated the tree listing for {Repository} at {Reference}; some paths were not listed",
                    repository.FullName,
                    reference);
            }

            var entries = new List<GitHubTreeEntry>();

            foreach (var element in tree.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind != JsonValueKind.String
                    || pathElement.GetString() is not { Length: > 0 } entryPath)
                {
                    continue;
                }

                entries.Add(new GitHubTreeEntry(
                    entryPath,
                    element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        ? type.GetString()!
                        : "blob",
                    element.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String
                        ? sha.GetString()!
                        : string.Empty,
                    element.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : null));
            }

            return entries;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetBlobTextAsync(
        GitHubRepository repository,
        string sha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/git/blobs/{Uri.EscapeDataString(sha)}",
            cancellationToken);

        if (json is null)
        {
            return string.Empty;
        }

        using (json)
        {
            if (!json.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var encoding = json.RootElement.TryGetProperty("encoding", out var encodingElement)
                           && encodingElement.ValueKind == JsonValueKind.String
                ? encodingElement.GetString()
                : "base64";

            return string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase)
                ? content.GetString() ?? string.Empty
                : DecodeBase64(content.GetString() ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public async Task CreateBranchAsync(
        GitHubRepository repository,
        string branch,
        string fromSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSha);

        using (await SendAsync(
                   repository,
                   GitHubTokenScope.Contribute,
                   () => Json(
                       HttpMethod.Post,
                       $"repos/{repository.Owner}/{repository.Name}/git/refs",
                       new { @ref = $"refs/heads/{branch}", sha = fromSha }),
                   cancellationToken))
        {
        }
    }

    /// <inheritdoc />
    public async Task<GitHubCommitResult> CommitFilesAsync(
        GitHubRepository repository,
        string branch,
        string message,
        IReadOnlyList<GitHubFileEdit> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            throw new ArgumentException("A commit must carry at least one file.", nameof(files));
        }

        // One commit rather than one per file. The contents API would produce a commit per write,
        // which turns a three-file scope proposal into a three-commit pull request for no reason.
        var headSha = await GetBranchHeadShaAsync(repository, branch, cancellationToken)
                      ?? throw new GitHubApiException(
                          $"Branch '{branch}' does not exist in {repository.FullName}, so nothing can be committed to it.");

        var baseTreeSha = await GetCommitTreeShaAsync(repository, headSha, cancellationToken);

        var blobs = new List<object>(files.Count);

        foreach (var file in files)
        {
            using var blob = await SendAsync(
                repository,
                GitHubTokenScope.Contribute,
                () => Json(
                    HttpMethod.Post,
                    $"repos/{repository.Owner}/{repository.Name}/git/blobs",
                    new { content = file.Text, encoding = "utf-8" }),
                cancellationToken);

            blobs.Add(new
            {
                path = file.Path,
                mode = "100644",
                type = "blob",
                sha = ReadString(blob.RootElement, "sha")
                      ?? throw new GitHubApiException("GitHub's blob response carried no SHA."),
            });
        }

        string treeSha;

        using (var tree = await SendAsync(
                   repository,
                   GitHubTokenScope.Contribute,
                   () => Json(
                       HttpMethod.Post,
                       $"repos/{repository.Owner}/{repository.Name}/git/trees",
                       new { base_tree = baseTreeSha, tree = blobs }),
                   cancellationToken))
        {
            treeSha = ReadString(tree.RootElement, "sha")
                      ?? throw new GitHubApiException("GitHub's tree response carried no SHA.");
        }

        string commitSha;

        using (var commit = await SendAsync(
                   repository,
                   GitHubTokenScope.Contribute,
                   () => Json(
                       HttpMethod.Post,
                       $"repos/{repository.Owner}/{repository.Name}/git/commits",
                       new { message, tree = treeSha, parents = new[] { headSha } }),
                   cancellationToken))
        {
            commitSha = ReadString(commit.RootElement, "sha")
                        ?? throw new GitHubApiException("GitHub's commit response carried no SHA.");
        }

        using (await SendAsync(
                   repository,
                   GitHubTokenScope.Contribute,
                   () => Json(
                       HttpMethod.Patch,
                       $"repos/{repository.Owner}/{repository.Name}/git/refs/heads/{EscapePath(branch)}",
                       new { sha = commitSha, force = false }),
                   cancellationToken))
        {
        }

        return new GitHubCommitResult(commitSha, branch);
    }

    /// <inheritdoc />
    public async Task<GitHubPullRequestResult> OpenPullRequestAsync(
        GitHubRepository repository,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        using var document = await SendAsync(
            repository,
            GitHubTokenScope.Contribute,
            () => Json(
                HttpMethod.Post,
                $"repos/{repository.Owner}/{repository.Name}/pulls",
                new { title, head = headBranch, @base = baseBranch, body = body ?? string.Empty }),
            cancellationToken);

        var root = document.RootElement;

        var number = root.TryGetProperty("number", out var numberElement)
                     && numberElement.ValueKind == JsonValueKind.Number
            ? numberElement.GetInt32()
            : throw new GitHubApiException("GitHub's pull request response carried no number.");

        var headSha = root.TryGetProperty("head", out var head)
                      && head.ValueKind == JsonValueKind.Object
            ? ReadString(head, "sha") ?? string.Empty
            : string.Empty;

        return new GitHubPullRequestResult(
            number,
            ReadString(root, "html_url") ?? $"https://github.com/{repository.FullName}/pull/{number}",
            headSha,
            headBranch);
    }

    private async Task<string> GetCommitTreeShaAsync(
        GitHubRepository repository,
        string commitSha,
        CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            () => Json(
                HttpMethod.Get,
                $"repos/{repository.Owner}/{repository.Name}/git/commits/{Uri.EscapeDataString(commitSha)}",
                content: null),
            cancellationToken);

        return document.RootElement.TryGetProperty("tree", out var tree)
               && tree.ValueKind == JsonValueKind.Object
               && ReadString(tree, "sha") is { Length: > 0 } sha
            ? sha
            : throw new GitHubApiException($"GitHub's commit {commitSha} carried no tree SHA.");
    }

    private async Task<JsonDocument?> GetJsonOrNullAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                repository,
                scope,
                () => Json(HttpMethod.Get, path, content: null),
                cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // "Not there" is an ordinary answer for a `.charter/` read, not a failure. Section 9
            // onboards repositories that have never heard of Charter.
            return null;
        }
    }

    private static (HttpMethod Method, string Path, object? Content) Json(
        HttpMethod method,
        string path,
        object? content) => (method, path, content);

    private async Task<JsonDocument> SendAsync(
        GitHubRepository repository,
        GitHubTokenScope scope,
        Func<(HttpMethod Method, string Path, object? Content)> describe,
        CancellationToken cancellationToken)
    {
        // One retry, and only on an auth failure: a cached token that GitHub has since revoked is
        // the one transient error worth retrying, and retrying a write on anything else risks
        // committing twice.
        for (var attempt = 0; ; attempt++)
        {
            var (method, path, content) = describe();
            var token = await _tokens.GetInstallationTokenAsync(repository, scope, cancellationToken);

            using var request = new HttpRequestMessage(method, new Uri(_options.ApiBaseUrl, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token.Reveal());
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion);

            if (content is not null)
            {
                request.Content = JsonContent.Create(content, content.GetType());
            }

            var client = _httpClientFactory.CreateClient(GitHubAppTokenProvider.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (attempt == 0 && GitHubStatus.IsAuthFailure(response.StatusCode))
            {
                _tokens.Invalidate(repository, scope);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw GitHubApiException.ForResponse(response, request);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(body))
            {
                return JsonDocument.Parse("{}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new GitHubApiException(
                    $"GitHub's answer to {method.Method} {path} was not JSON.",
                    ex);
            }
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string DecodeBase64(string value)
    {
        var compact = string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(compact));
        }
        catch (FormatException ex)
        {
            throw new GitHubApiException("GitHub returned file content that was not valid base64.", ex);
        }
    }

    /// <summary>
    /// Escapes each segment and leaves the separators alone.
    /// </summary>
    /// <remarks>
    /// Branch names contain slashes — <c>charter/onboarding</c> is one — and so do file paths.
    /// Escaping the whole string would send <c>charter%2Fonboarding</c>, which GitHub reads as a
    /// branch with a slash in its name rather than as the ref <c>heads/charter/onboarding</c>.
    /// </remarks>
    private static string EscapePath(string path)
        => string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
