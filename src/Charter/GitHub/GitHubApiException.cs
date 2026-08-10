using System.Net;

namespace Charter.GitHub;

/// <summary>
/// GitHub refused, or answered something Charter cannot act on.
/// </summary>
/// <remarks>
/// The message carries the status, the method and the path, and never the response body: a GitHub
/// error body can echo request content, and the one request whose content must never reach a log is
/// the installation token exchange (section 20b.2).
/// </remarks>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitHubApiException()
        : base("The GitHub API call failed.")
    {
    }

    private GitHubApiException(string message, HttpStatusCode status, string method, string path)
        : base(message)
    {
        Status = status;
        Method = method;
        Path = path;
    }

    /// <summary>The status GitHub returned, when the failure was an HTTP response.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>The verb of the failed call.</summary>
    public string? Method { get; }

    /// <summary>The path of the failed call, never the query string.</summary>
    public string? Path { get; }

    /// <summary>Builds the exception for a failed response, without reading the body.</summary>
    public static GitHubApiException ForResponse(HttpResponseMessage response, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        var method = request.Method.Method;
        var path = request.RequestUri?.AbsolutePath ?? "(unknown)";

        return new GitHubApiException(
            $"GitHub answered {(int)response.StatusCode} {response.StatusCode} to {method} {path}.",
            response.StatusCode,
            method,
            path);
    }
}
