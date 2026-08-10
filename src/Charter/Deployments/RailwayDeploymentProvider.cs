using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// Railway PR Environments — the only Phase 1 implementation of <see cref="IDeploymentProvider"/>
/// (change spec 001, implementation discipline).
/// </summary>
/// <remarks>
/// <para>
/// A Railway PR environment replicates every service, database and variable from a base environment
/// into an isolated ephemeral environment with fresh URLs (section 18). Two consequences drive this
/// implementation. The first is that the base environment is a security decision, enforced in
/// <see cref="RailwayOptions"/> rather than documented in a footnote. The second is that Railway will
/// not deploy a change request branch from an account outside the workspace unless it has been
/// invited with that account — and that does not surface as an error anywhere. It surfaces as a
/// preview that never arrives, which is why <see cref="ObserveAsync"/> is willing to say so out loud.
/// </para>
/// <para>
/// Everything read from Railway is read defensively. The GraphQL schema is Railway's, it changes on
/// Railway's schedule, and a field that moves must degrade to "not yet" rather than throw: a preview
/// binding that crashes on an unexpected shape is worse than one that reports nothing, because the
/// requester's card is the last thing in the pipeline and it is the only thing they see.
/// </para>
/// </remarks>
public sealed class RailwayDeploymentProvider : IDeploymentProvider
{
    /// <summary>The named <see cref="HttpClient"/> this provider resolves.</summary>
    public const string HttpClientName = "charter.railway";

    /// <summary>The value written to <see cref="Deployment.Provider"/>.</summary>
    public const string ProviderId = "railway";

    /// <summary>
    /// The logins Railway's own integration comments under. The <c>[bot]</c> suffix is matched
    /// loosely by <see cref="PreviewCommentParser.IsFrom"/>.
    /// </summary>
    public static IReadOnlyList<string> BotLogins { get; } = ["railway", "railway-app", "railwayapp"];

    private const string EnvironmentsQuery = """
        query Environments($projectId: String!) {
          environments(projectId: $projectId) {
            edges { node { id name isEphemeral meta { branch prNumber prRepo } } }
          }
        }
        """;

    private const string DeploymentsQuery = """
        query Deployments($projectId: String!, $environmentId: String!) {
          deployments(first: 20, input: { projectId: $projectId, environmentId: $environmentId }) {
            edges { node { id status staticUrl url createdAt meta } }
          }
        }
        """;

    private const string EnvironmentDeleteMutation = """
        mutation EnvironmentDelete($id: String!) { environmentDelete(id: $id) }
        """;

    private readonly IHttpClientFactory _clients;
    private readonly DeploymentOptions _options;
    private readonly RailwayOptions _railway;
    private readonly TimeProvider _clock;
    private readonly ILogger<RailwayDeploymentProvider> _logger;

    public RailwayDeploymentProvider(
        IHttpClientFactory clients,
        DeploymentOptions options,
        TimeProvider clock,
        ILogger<RailwayDeploymentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _railway = options.Railway
            ?? throw new ArgumentException(
                "Railway is not configured. Set CHARTER_DEPLOYMENT_PROVIDER=railway with " +
                "CHARTER_RAILWAY_TOKEN, CHARTER_RAILWAY_PROJECT_ID and CHARTER_RAILWAY_BASE_ENVIRONMENT.",
                nameof(options));
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public DeploymentProviderCapabilities Capabilities { get; } = new(
        Poll: true,
        CommentParsing: true,
        Teardown: true,

        // Railway keeps a PR environment for as long as the change request is open. That is not an
        // expiry, so section 27.7's countdown comes from CHARTER_PREVIEW_TTL_HOURS instead.
        NativeExpiry: false);

    /// <inheritdoc />
    public TimeSpan? PreviewLifetime => _options.PreviewTtl <= TimeSpan.Zero ? null : _options.PreviewTtl;

    /// <summary>The base environment previews are replicated from.</summary>
    public string BaseEnvironment => _railway.BaseEnvironment;

    /// <inheritdoc />
    public async Task<DeploymentObservation> ObserveAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var environments = await QueryAsync(
            EnvironmentsQuery,
            new Dictionary<string, object?> { ["projectId"] = _railway.ProjectId },
            cancellationToken);

        if (environments is null)
        {
            return DeploymentObservation.NotYet("Railway did not answer");
        }

        using (environments)
        {
            var (environmentId, ephemeralCount) = FindEnvironment(environments.RootElement, target);

            if (environmentId is null)
            {
                return NoEnvironment(target, ephemeralCount);
            }

            var deployments = await QueryAsync(
                DeploymentsQuery,
                new Dictionary<string, object?>
                {
                    ["projectId"] = _railway.ProjectId,
                    ["environmentId"] = environmentId,
                },
                cancellationToken);

            if (deployments is null)
            {
                return DeploymentObservation.NotYet("Railway did not answer");
            }

            using (deployments)
            {
                return ReadDeployment(deployments.RootElement, target, environmentId);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Accepts a comment from Railway's own integration and nothing else. An operator whose own
    /// automation comments a URL is served by <c>POST /api/deployments/{prSha}</c>, which is a
    /// documented interface rather than a guess at somebody's wording.
    /// </remarks>
    public DeploymentObservation ReadComment(DeploymentComment comment, DeploymentTarget target)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(target);

        if (!PreviewCommentParser.IsFrom(comment, BotLogins))
        {
            return DeploymentObservation.NotYet("this comment was not written by Railway");
        }

        return PreviewCommentParser.Read(comment, ProviderId, IsPreviewUrl);
    }

    /// <inheritdoc />
    public async Task<DeploymentTeardownResult> TeardownAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var environments = await QueryAsync(
            EnvironmentsQuery,
            new Dictionary<string, object?> { ["projectId"] = _railway.ProjectId },
            cancellationToken);

        if (environments is null)
        {
            return DeploymentTeardownResult.NothingToDo("Railway did not answer");
        }

        string? environmentId;

        using (environments)
        {
            (environmentId, _) = FindEnvironment(environments.RootElement, target);
        }

        if (environmentId is null)
        {
            // Railway reclaims a PR environment when the change request closes, so losing the race is
            // the expected outcome rather than a fault.
            return DeploymentTeardownResult.NothingToDo(
                $"Railway has no preview environment for change request {target.Number}");
        }

        var deleted = await QueryAsync(
            EnvironmentDeleteMutation,
            new Dictionary<string, object?> { ["id"] = environmentId },
            cancellationToken);

        if (deleted is null)
        {
            return DeploymentTeardownResult.NothingToDo("Railway refused the teardown");
        }

        deleted.Dispose();

        _logger.LogInformation(
            "Tore down the Railway preview environment {EnvironmentId} for change request {Number}",
            environmentId,
            target.Number);

        return DeploymentTeardownResult.Confirmed;
    }

    /// <summary>
    /// Whether a URL from a Railway comment is a preview rather than a link to Railway itself.
    /// </summary>
    /// <remarks>
    /// Railway's comment links its own dashboard next to the deployment, and a dashboard URL is the
    /// last thing to put on a requester's card: it asks them to log into a platform they have no
    /// account on, to look at a preview they were told they could click.
    /// </remarks>
    public static bool IsPreviewUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var host = url.Host.ToLowerInvariant();

        return host is not ("railway.app" or "www.railway.app" or "railway.com" or "www.railway.com");
    }

    private DeploymentObservation NoEnvironment(DeploymentTarget target, int ephemeralCount)
    {
        if (target.HeadSeenAt is not { } seen)
        {
            return DeploymentObservation.NotYet("Railway has no preview environment for this change request yet");
        }

        var waited = _clock.GetUtcNow() - seen;

        if (waited < _railway.MissingEnvironmentGrace)
        {
            return DeploymentObservation.NotYet("Railway has not created the preview environment yet");
        }

        var minutes = (int)waited.TotalMinutes;
        var author = string.IsNullOrWhiteSpace(target.AuthorLogin) ? "the author" : target.AuthorLogin;

        // The section 18 case that presents as nothing at all. Absence is the only signal available,
        // so the wording says what is known, what is likely, and what to do about it — rather than
        // asserting a cause Railway never reported.
        var explanation = ephemeralCount > 0
            ? $"Railway has preview environments for other change requests in this project but none " +
              $"for {target.Number} after {minutes} minutes. Railway will not deploy a change request " +
              $"branch from an account outside the workspace unless it has been invited with that " +
              $"account — invite {author} to the Railway workspace, or bind the preview through " +
              "POST /api/deployments/{prSha} instead (section 18)."
            : $"Railway has created no preview environment for change request {target.Number} after " +
              $"{minutes} minutes, and none for any other change request in this project. Either PR " +
              "environments are switched off for the project, or the change request comes from an " +
              $"account outside the workspace — Railway will not deploy one unless {author} has been " +
              "invited with that account (section 18).";

        return DeploymentObservation.Blocked(explanation);
    }

    /// <summary>Finds the ephemeral environment for a change request, and counts the rest.</summary>
    private static (string? EnvironmentId, int EphemeralCount) FindEnvironment(
        JsonElement root,
        DeploymentTarget target)
    {
        string? match = null;
        var ephemeral = 0;

        foreach (var node in Nodes(root, "environments"))
        {
            var isEphemeral = Bool(node, "isEphemeral") ?? false;
            if (isEphemeral)
            {
                ephemeral++;
            }

            if (match is not null || Text(node, "id") is not { } id)
            {
                continue;
            }

            var meta = Object(node, "meta");
            var name = Text(node, "name");

            var isThisOne = Number(meta, "prNumber") == target.Number
                            || (target.HeadBranch is not null
                                && string.Equals(Text(meta, "branch"), target.HeadBranch, StringComparison.OrdinalIgnoreCase))
                            || (target.HeadBranch is not null
                                && string.Equals(name, target.HeadBranch, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(
                                name,
                                string.Create(CultureInfo.InvariantCulture, $"pr-{target.Number}"),
                                StringComparison.OrdinalIgnoreCase);

            if (isThisOne)
            {
                match = id;
            }
        }

        return (match, ephemeral);
    }

    private DeploymentObservation ReadDeployment(JsonElement root, DeploymentTarget target, string environmentId)
    {
        JsonElement? chosen = null;
        var sawAny = false;

        foreach (var node in Nodes(root, "deployments"))
        {
            sawAny = true;

            var commit = Text(Object(node, "meta"), "commitHash");

            if (commit is not null
                && !commit.StartsWith(target.HeadSha, StringComparison.OrdinalIgnoreCase)
                && !target.HeadSha.StartsWith(commit, StringComparison.OrdinalIgnoreCase))
            {
                // A deployment of a different commit says nothing about this one. Reporting it would
                // put an older build's URL on a card that claims to show this change.
                continue;
            }

            // The list arrives newest first; the first commit match is the current one.
            chosen ??= node;
        }

        if (chosen is not { } deployment)
        {
            return DeploymentObservation.NotYet(sawAny
                ? "Railway has not deployed this commit yet"
                : "Railway's preview environment has no deployments yet");
        }

        var status = Text(deployment, "status");

        // Railway's vocabulary is already in the synonym table the generic webhook uses, so the
        // translation lives in one place rather than once per provider.
        if (!DeploymentBinder.TryParseState(status, out var state))
        {
            _logger.LogDebug("Railway reported deployment status {Status}, which Charter does not map", status);
            state = DeploymentState.Pending;
        }

        var url = PreviewUrl(deployment);

        if (state == DeploymentState.Ready && url is null)
        {
            // A successful deployment with nowhere to click is not ready as far as a requester is
            // concerned, and Deployment.Report would refuse it anyway.
            return DeploymentObservation.NotYet("Railway reported a successful deployment with no URL yet");
        }

        return DeploymentObservation.Reported(new DeploymentReport(
            ProviderId,
            state,
            url,
            _options.ExpiryFor(_clock.GetUtcNow(), PreviewLifetime),
            Text(deployment, "id") ?? environmentId,
            $"Railway status {status ?? "unknown"}"));
    }

    private static string? PreviewUrl(JsonElement deployment)
    {
        var raw = Text(deployment, "staticUrl") ?? Text(deployment, "url");

        if (raw is null)
        {
            return null;
        }

        // Railway returns a bare host for staticUrl. A card that renders that verbatim produces a
        // link the browser resolves against Charter's own origin.
        var absolute = raw.Contains("://", StringComparison.Ordinal) ? raw : $"https://{raw}";

        return Uri.TryCreate(absolute, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.ToString()
            : null;
    }

    /// <summary>Sends one GraphQL document, returning null for anything that did not work.</summary>
    /// <remarks>
    /// Every failure mode collapses to null: an HTTP error, a timeout, a body that is not JSON, a
    /// GraphQL <c>errors</c> array. Callers turn null into "not yet", because that is what a preview
    /// Charter could not ask about actually is.
    /// </remarks>
    private async Task<JsonDocument?> QueryAsync(
        string document,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, _railway.ApiUrl)
        {
            Content = JsonContent.Create(new { query = document, variables }),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _railway.Token.Reveal());

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Railway answered {Status} for a deployment query. Check CHARTER_RAILWAY_TOKEN and " +
                    "CHARTER_RAILWAY_PROJECT_ID.",
                    (int)response.StatusCode);

                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonDocument.Parse(body);

            if (parsed.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                _logger.LogWarning(
                    "Railway returned GraphQL errors for a deployment query: {Errors}",
                    errors.ToString());

                parsed.Dispose();
                return null;
            }

            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach Railway to bind a preview");
            return null;
        }
    }

    /// <summary>Walks <c>data.{field}.edges[].node</c>, tolerating every level being absent.</summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement root, string field)
    {
        if (Object(Object(root, "data"), field) is not { } connection
            || !connection.TryGetProperty("edges", out var edges)
            || edges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (Object(edge, "node") is { } node)
            {
                yield return node;
            }
        }
    }

    private static JsonElement? Object(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? Text(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static int? Number(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool? Bool(JsonElement? parent, string name)
        => parent is { ValueKind: JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
