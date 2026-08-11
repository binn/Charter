using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Charter.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Charter.Updates;

/// <summary>Where the release list comes from. The seam a test replaces to stay offline.</summary>
public interface IReleaseSource
{
    /// <summary>
    /// Reads the published releases, newest first.
    /// </summary>
    /// <returns>
    /// The releases, or <see langword="null"/> when the question could not be asked — offline,
    /// air-gapped, rate-limited, or answered with something that is not a release list. Section 28
    /// requires those to degrade silently, so the distinction between "nothing newer" and "could not
    /// look" is carried in the return value rather than in an exception.
    /// </returns>
    Task<IReadOnlyList<Release>?> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The one outbound request Charter makes on its own initiative (sections 28, 19).
/// </summary>
/// <remarks>
/// <para>
/// An unauthenticated <c>GET</c> against the public GitHub Releases API. It carries no query about
/// this instance, no credential, no cookie, and a fixed <c>User-Agent</c> of <c>Charter</c> with no
/// version in it — GitHub requires a user agent, and a version string would tell it which release each
/// instance runs, which is precisely the "no data about your instance" promise in
/// <c>docs/privacy.md</c>. The comparison against the running build happens locally, after the
/// response arrives.
/// </para>
/// <para>
/// The client comes from <c>IHttpClientFactory</c> so that <c>CHARTER_DEMO</c>'s kill switch, which is
/// installed on every named client, blocks this like any other egress (section 30.6).
/// </para>
/// <para>
/// Section 16: the response is untrusted input from the network. The host is pinned, redirects are the
/// handler's default (same-scheme, and the response is parsed not executed), the body is read under a
/// byte cap so a hostile or broken endpoint cannot exhaust memory, and every field is treated as text.
/// Nothing here is written to disk or passed to a shell.
/// </para>
/// </remarks>
public sealed class GitHubReleaseSource : IReleaseSource
{
    /// <summary>The named client, so demo mode and OpenTelemetry both see it.</summary>
    public const string HttpClientName = "charter-update-check";

    /// <summary>
    /// The user agent sent. Deliberately constant: it identifies the software, never the instance.
    /// </summary>
    public const string UserAgent = "Charter";

    /// <summary>The most bytes read from the response before giving up.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly IHttpClientFactory _factory;
    private readonly UpdateCheckOptions _options;
    private readonly ILogger<GitHubReleaseSource> _logger;

    public GitHubReleaseSource(
        IHttpClientFactory factory,
        UpdateCheckOptions options,
        ILogger<GitHubReleaseSource> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Release>?> ListAsync(CancellationToken cancellationToken = default)
    {
        var repository = _options.Repository;

        if (repository is null)
        {
            // The source URL this build was compiled with is not a GitHub repository, so there is no
            // releases endpoint to read. Nothing to warn about on a schedule; a fork that rebranded
            // (section 24) is entitled to have no update check.
            _logger.LogDebug(
                "No GitHub repository could be derived from {SourceUrl}; the release check is inert",
                BuildInfo.SourceUrl);
            return null;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.github.com/repos/{repository}/releases?per_page={_options.PageSize}");

        try
        {
            using var client = _factory.CreateClient(HttpClientName);
            client.Timeout = _options.RequestTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Rate limiting and outages both land here, both on a daily schedule. Debug, not
                // warning: section 28 is explicit that an instance which cannot reach GitHub must not
                // teach its operator to ignore the log.
                _logger.LogDebug(
                    "The release check answered {StatusCode}; keeping the previous result",
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var body = await ReadCappedAsync(stream, cancellationToken);

            return body is null ? null : Parse(body);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or TaskCanceledException
                                              or JsonException
                                              or InvalidOperationException
                                              or IOException
                                              or UriFormatException
                                              or Charter.Hosting.DemoModeException)
        {
            // Offline, DNS-less, TLS-intercepted, or blocked by demo mode. All of them are "could not
            // look", none of them is an error the operator has to act on, and one of them happens
            // every day on an air-gapped instance. Demo mode is named explicitly rather than left to
            // a catch-all: section 30.6's handler throws its own type, and an unregistered check that
            // somebody later wires anyway must degrade like every other unreachable network.
            _logger.LogDebug(exception, "The release check could not reach GitHub; keeping the previous result");
            return null;
        }
    }

    /// <summary>Parses the array GitHub returns, skipping anything that is not a usable release.</summary>
    internal static IReadOnlyList<Release> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var releases = new List<Release>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A draft is not published, so no instance should ever be offered it.
            if (Boolean(element, "draft"))
            {
                continue;
            }

            var tag = String(element, "tag_name");
            var version = ReleaseVersion.TryParse(tag);

            if (tag is null || version is null)
            {
                // CHANGELOG.md: a tag that does not parse as a version is ignored. A mistyped tag
                // fails quietly here rather than announcing a release nobody can install.
                continue;
            }

            releases.Add(new Release(
                tag,
                version,
                String(element, "name") ?? tag,
                String(element, "html_url") ?? string.Empty,
                String(element, "body") ?? string.Empty,
                Boolean(element, "prerelease"),
                Timestamp(element, "published_at")));
        }

        return releases;
    }

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>, returning null if the body exceeds it.</summary>
    private static async Task<string?> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Boolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Timestamp(JsonElement element, string name)
        => String(element, name) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
