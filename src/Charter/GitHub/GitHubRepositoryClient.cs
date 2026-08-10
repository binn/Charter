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

    /// <summary>Moves a branch to a commit GitHub already has. Creates nothing.</summary>
    Task<GitHubCommitResult> UpdateBranchAsync(
        GitHubRepository repository,
        string branch,
        string sha,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>One pull request as GitHub currently sees it, or null when there is no such number.</summary>
    Task<GitHubPullRequestDetail?> GetPullRequestAsync(
        GitHubRepository repository,
        int number,
        CancellationToken cancellationToken = default);

    /// <summary>Comments on a pull request. Section 14's engineer recap lands here.</summary>
    Task CommentOnPullRequestAsync(
        GitHubRepository repository,
        int number,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>Adds labels to a pull request (sections 7.5, 15). Existing labels are kept.</summary>
    Task<IReadOnlyList<string>> AddLabelsAsync(
        GitHubRepository repository,
        int number,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default);

    /// <summary>Compares two revisions: how far apart, and which files differ (section 17).</summary>
    Task<GitHubComparison> CompareAsync(
        GitHubRepository repository,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the branch protection rule, if there is one (change spec 001 part A.5).
    /// </summary>
    /// <remarks>
    /// Never throws on "no rule" or on "not allowed to look". Both are answers the onboarding check
    /// needs, and both mean the same thing to an operator: the merge gate is not verified.
    /// </remarks>
    Task<GitHubBranchProtection> GetBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a branch protection rule. Requires the administration scope.</summary>
    Task ApplyBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        int requiredApprovals,
        bool requireCodeOwnerReview,
        bool dismissStaleReviews,
        bool enforceForAdministrators,
        CancellationToken cancellationToken = default);

    /// <summary>Registers the repository webhook, or reports the one already pointing there.</summary>
    Task<GitHubWebhookHook> RegisterWebhookAsync(
        GitHubRepository repository,
        Uri callbackUrl,
        string secret,
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a repository in an organisation (section 26.10).
    /// </summary>
    /// <remarks>
    /// Takes an installation id rather than a <see cref="GitHubRepository"/> because the repository
    /// does not exist yet, and a token scoped to one repository cannot be minted for a name GitHub
    /// has never heard of. This is the one call in the client that reaches past a single repository,
    /// and the token it uses is never handed to a runner.
    /// </remarks>
    Task<GitHubRepositorySummary> CreateRepositoryAsync(
        long installationId,
        string owner,
        string name,
        bool isPrivate,
        string? description,
        CancellationToken cancellationToken = default);

    /// <summary>Transfers a repository out of the sandbox organisation (section 26.9).</summary>
    Task<GitHubRepositorySummary> TransferRepositoryAsync(
        GitHubRepository repository,
        string newOwner,
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
            headBranch,
            root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? ReadString(user, "login")
                : null);
    }

    /// <inheritdoc />
    public async Task<GitHubCommitResult> UpdateBranchAsync(
        GitHubRepository repository,
        string branch,
        string sha,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        using var document = await SendAsync(
            repository,
            GitHubTokenScope.Contribute,
            () => Json(
                HttpMethod.Patch,
                $"repos/{repository.Owner}/{repository.Name}/git/refs/heads/{EscapePath(branch)}",
                new { sha, force }),
            cancellationToken);

        return new GitHubCommitResult(
            document.RootElement.TryGetProperty("object", out var target)
            && ReadString(target, "sha") is { Length: > 0 } moved
                ? moved
                : sha,
            branch);
    }

    /// <inheritdoc />
    public async Task<GitHubPullRequestDetail?> GetPullRequestAsync(
        GitHubRepository repository,
        int number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/pulls/{number}",
            cancellationToken);

        if (json is null)
        {
            return null;
        }

        using (json)
        {
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var head = root.TryGetProperty("head", out var headElement) ? headElement : default;
            var @base = root.TryGetProperty("base", out var baseElement) ? baseElement : default;

            return new GitHubPullRequestDetail(
                number,
                ReadString(root, "html_url") ?? $"https://github.com/{repository.FullName}/pull/{number}",
                ReadString(root, "state") ?? "open",
                root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
                root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                ReadString(head, "sha") ?? string.Empty,
                ReadString(head, "ref"),
                ReadString(@base, "ref"),
                ReadLabels(root),
                root.TryGetProperty("user", out var author) && author.ValueKind == JsonValueKind.Object
                    ? ReadString(author, "login")
                    : null);
        }
    }

    /// <inheritdoc />
    public async Task CommentOnPullRequestAsync(
        GitHubRepository repository,
        int number,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        // The issues endpoint, not the reviews one, deliberately. A review carries a verdict —
        // approve or request changes — and section 14 is emphatic that the recap is an orientation
        // aid rather than a judgement. Charter has no opinion to record and no standing to record it.
        using (await SendAsync(
                   repository,
                   GitHubTokenScope.Contribute,
                   () => Json(
                       HttpMethod.Post,
                       $"repos/{repository.Owner}/{repository.Name}/issues/{number}/comments",
                       new { body }),
                   cancellationToken))
        {
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AddLabelsAsync(
        GitHubRepository repository,
        int number,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            return [];
        }

        using var document = await SendAsync(
            repository,
            GitHubTokenScope.Contribute,
            () => Json(
                HttpMethod.Post,
                $"repos/{repository.Owner}/{repository.Name}/issues/{number}/labels",
                new { labels }),
            cancellationToken);

        var applied = new List<string>(labels.Count);

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadString(element, "name") is { Length: > 0 } name)
                {
                    applied.Add(name);
                }
            }
        }

        return applied.Count > 0 ? applied : labels;
    }

    /// <inheritdoc />
    public async Task<GitHubComparison> CompareAsync(
        GitHubRepository repository,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(headRevision);

        var json = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.ReadOnly,
            $"repos/{repository.Owner}/{repository.Name}/compare/"
            + $"{EscapePath(baseRevision)}...{EscapePath(headRevision)}",
            cancellationToken);

        if (json is null)
        {
            return new GitHubComparison(0, 0, []);
        }

        using (json)
        {
            var root = json.RootElement;
            var files = new List<string>();

            if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (ReadString(element, "filename") is { Length: > 0 } filename)
                    {
                        files.Add(filename);
                    }

                    // A rename changes two paths, and section 17's overlap test has to see both or
                    // it will call a rename-versus-edit collision disjoint.
                    if (ReadString(element, "previous_filename") is { Length: > 0 } previous)
                    {
                        files.Add(previous);
                    }
                }
            }

            return new GitHubComparison(ReadInt(root, "ahead_by"), ReadInt(root, "behind_by"), files);
        }
    }

    /// <inheritdoc />
    public async Task<GitHubBranchProtection> GetBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        JsonDocument? json;

        try
        {
            json = await SendAsync(
                repository,
                GitHubTokenScope.Inspect,
                () => Json(
                    HttpMethod.Get,
                    $"repos/{repository.Owner}/{repository.Name}/branches/{EscapePath(branch)}/protection",
                    content: null),
                cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // GitHub's answer for "this branch has no protection rule" — and also for "there is no
            // such branch". Both mean nothing stands between a person and a merge.
            return new GitHubBranchProtection(false, Detail: $"no branch protection rule covers '{branch}'");
        }
        catch (GitHubApiException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Never reported as protected. An installation without `administration: read` cannot be
            // asked, and "cannot be asked" is not "is protected" (change spec 001 part A.5).
            return new GitHubBranchProtection(
                false,
                Detail: "Charter's GitHub App installation may not read branch protection here, so the "
                        + "merge gate is not verified");
        }

        using (json)
        {
            var root = json.RootElement;
            var reviews = root.TryGetProperty("required_pull_request_reviews", out var element)
                          && element.ValueKind == JsonValueKind.Object
                ? element
                : (JsonElement?)null;

            return new GitHubBranchProtection(
                true,
                reviews is { } required ? ReadInt(required, "required_approving_review_count") : null,
                reviews is { } owners && ReadBool(owners, "require_code_owner_reviews"),
                reviews is { } stale && ReadBool(stale, "dismiss_stale_reviews"),
                root.TryGetProperty("enforce_admins", out var admins)
                && admins.ValueKind == JsonValueKind.Object
                && ReadBool(admins, "enabled"),
                reviews is null
                    ? $"'{branch}' is protected, but the rule does not require a review before merge"
                    : $"'{branch}' requires review before merge");
        }
    }

    /// <inheritdoc />
    public async Task ApplyBranchProtectionAsync(
        GitHubRepository repository,
        string branch,
        int requiredApprovals,
        bool requireCodeOwnerReview,
        bool dismissStaleReviews,
        bool enforceForAdministrators,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredApprovals);

        using (await SendAsync(
                   repository,
                   GitHubTokenScope.Administer,
                   () => Json(
                       HttpMethod.Put,
                       $"repos/{repository.Owner}/{repository.Name}/branches/{EscapePath(branch)}/protection",
                       new
                       {
                           required_status_checks = (object?)null,
                           enforce_admins = enforceForAdministrators,
                           required_pull_request_reviews = new
                           {
                               required_approving_review_count = requiredApprovals,
                               require_code_owner_reviews = requireCodeOwnerReview,
                               dismiss_stale_reviews = dismissStaleReviews,
                           },
                           restrictions = (object?)null,
                       }),
                   cancellationToken))
        {
        }
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookHook> RegisterWebhookAsync(
        GitHubRepository repository,
        Uri callbackUrl,
        string secret,
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(callbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(events);

        var target = callbackUrl.ToString();

        // Idempotent by inspection rather than by catching a duplicate: GitHub happily creates a
        // second hook to the same URL, and two hooks mean every delivery arrives twice.
        var existing = await GetJsonOrNullAsync(
            repository,
            GitHubTokenScope.Webhooks,
            $"repos/{repository.Owner}/{repository.Name}/hooks",
            cancellationToken);

        if (existing is not null)
        {
            using (existing)
            {
                if (existing.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hook in existing.RootElement.EnumerateArray())
                    {
                        if (hook.TryGetProperty("config", out var config)
                            && string.Equals(ReadString(config, "url"), target, StringComparison.OrdinalIgnoreCase))
                        {
                            return new GitHubWebhookHook(
                                hook.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                                    ? id.GetInt64()
                                    : 0,
                                target,
                                false);
                        }
                    }
                }
            }
        }

        using var document = await SendAsync(
            repository,
            GitHubTokenScope.Webhooks,
            () => Json(
                HttpMethod.Post,
                $"repos/{repository.Owner}/{repository.Name}/hooks",
                new
                {
                    name = "web",
                    active = true,
                    events,
                    config = new { url = target, content_type = "json", secret, insecure_ssl = "0" },
                }),
            cancellationToken);

        return new GitHubWebhookHook(
            document.RootElement.TryGetProperty("id", out var created)
            && created.ValueKind == JsonValueKind.Number
                ? created.GetInt64()
                : 0,
            target,
            true);
    }

    /// <inheritdoc />
    public async Task<GitHubRepositorySummary> CreateRepositoryAsync(
        long installationId,
        string owner,
        string name,
        bool isPrivate,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var token = await _tokens.GetOrganizationTokenAsync(
            installationId,
            GitHubTokenScope.Administer,
            cancellationToken);

        using var document = await SendWithTokenAsync(
            token.Token.Reveal(),
            HttpMethod.Post,
            $"orgs/{Uri.EscapeDataString(owner)}/repos",
            new { name, @private = isPrivate, description, auto_init = true },
            cancellationToken);

        return ReadRepositorySummary(document.RootElement, $"{owner}/{name}");
    }

    /// <inheritdoc />
    public async Task<GitHubRepositorySummary> TransferRepositoryAsync(
        GitHubRepository repository,
        string newOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOwner);

        using var document = await SendAsync(
            repository,
            GitHubTokenScope.Administer,
            () => Json(
                HttpMethod.Post,
                $"repos/{repository.Owner}/{repository.Name}/transfer",
                new { new_owner = newOwner }),
            cancellationToken);

        return ReadRepositorySummary(document.RootElement, $"{newOwner}/{repository.Name}");
    }

    private static GitHubRepositorySummary ReadRepositorySummary(JsonElement root, string fallbackFullName)
        => new(
            ReadString(root, "full_name") ?? fallbackFullName,
            ReadString(root, "default_branch") ?? "main",
            !root.TryGetProperty("private", out var visibility) || visibility.ValueKind != JsonValueKind.False);

    private static IReadOnlyList<string> ReadLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var labels = new List<string>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } literal)
            {
                labels.Add(literal);
            }
            else if (ReadString(element, "name") is { Length: > 0 } name)
            {
                labels.Add(name);
            }
        }

        return labels;
    }

    private static int ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool ReadBool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    private async Task<JsonDocument> SendWithTokenAsync(
        string token,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.ApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion);

        if (content is not null)
        {
            request.Content = JsonContent.Create(content, content.GetType());
        }

        var client = _httpClientFactory.CreateClient(GitHubAppTokenProvider.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw GitHubApiException.ForResponse(response, request);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(body) ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);
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
