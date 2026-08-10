using System.Text;
using Charter.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>
/// The two inbound webhooks: GitHub's, and the provider-agnostic deployment one of section 18.
/// </summary>
/// <remarks>
/// Both routes are deliberately outside the authenticated API surface — they are called by machines
/// that have no Charter session — so each carries its own admission rule, stated at the top of its
/// handler rather than delegated to middleware that a future refactor could reorder away.
/// </remarks>
public static class GitHubWebhookEndpoints
{
    /// <summary>Where the GitHub App's webhook is pointed.</summary>
    public const string WebhookPath = "/api/github/webhook";

    /// <summary>Section 18's generic deployment webhook.</summary>
    public const string DeploymentPath = "/api/deployments/{prSha}";

    /// <summary>Maps the webhook routes.</summary>
    public static WebApplication MapCharterGitHub(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapWebhook(app);
        MapDeployments(app);

        return app;
    }

    private static void MapWebhook(WebApplication app)
    {
        app.MapPost(WebhookPath, async (
            HttpContext context,
            GitHubConfig config,
            GitHubOptions options,
            GitHubWebhookReceiver receiver,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger(typeof(GitHubWebhookEndpoints));

            // Read the body as bytes and verify before anything parses it. The signature covers the
            // exact bytes GitHub sent, so re-encoding first would both break verification and mean
            // an unsigned payload had already been through a parser.
            var payload = await ReadBodyAsync(context.Request, options.MaxWebhookBytes, cancellationToken);

            if (payload is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var signature = context.Request.Headers[GitHubWebhookSignature.HeaderName].ToString();

            if (!GitHubWebhookSignature.IsValid(config.WebhookSecret.Reveal(), payload, signature))
            {
                // One message for missing and for wrong, so the response cannot be used to work out
                // which. The log distinguishes them because the operator legitimately needs to.
                logger.LogWarning(
                    "Rejected a GitHub webhook delivery: the {Header} header was {State}",
                    GitHubWebhookSignature.HeaderName,
                    string.IsNullOrEmpty(signature) ? "absent" : "not a valid signature for the body");

                return Results.Unauthorized();
            }

            var eventName = context.Request.Headers[GitHubWebhookSignature.EventHeaderName].ToString();

            if (string.IsNullOrWhiteSpace(eventName))
            {
                return Results.BadRequest(new { error = "the X-GitHub-Event header is required" });
            }

            var delivery = GitHubWebhookDelivery.Parse(
                eventName,
                Encoding.UTF8.GetString(payload),
                context.Request.Headers[GitHubWebhookSignature.DeliveryHeaderName].ToString() is { Length: > 0 } id
                    ? id
                    : null);

            await receiver.DispatchAsync(delivery, cancellationToken);

            // 202 rather than 200: the delivery is accepted and acted on asynchronously by design.
            return Results.Accepted();
        });
    }

    private static void MapDeployments(WebApplication app)
    {
        app.MapPost(DeploymentPath, async (
            string prSha,
            DeploymentWebhookRequest body,
            DeploymentBinder binder,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "a body of { url, state, provider } is required" });
            }

            var result = await binder.ReportAsync(prSha, body, cancellationToken);

            return result.Outcome switch
            {
                DeploymentBindingOutcome.Recorded => Results.Accepted(),
                DeploymentBindingOutcome.UnknownCommit => Results.NotFound(new { error = result.Detail }),
                _ => Results.BadRequest(new { error = result.Detail }),
            };
        });
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes, returning <see langword="null"/> past that.
    /// </summary>
    /// <remarks>
    /// An unauthenticated endpoint that buffers whatever it is sent is a memory exhaustion primitive,
    /// and the signature cannot be checked without buffering, so the bound comes first.
    /// </remarks>
    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } declared && declared > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// Fans a verified delivery out to every registered <see cref="IGitHubWebhookListener"/>.
/// </summary>
/// <remarks>
/// Listeners are resolved from a scope created here rather than from the request's, so a listener
/// that wants a <c>DbContext</c> gets one whose lifetime it controls, and so one slow listener does
/// not hold the request's scope open.
/// </remarks>
public sealed class GitHubWebhookReceiver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GitHubWebhookReceiver> _logger;

    public GitHubWebhookReceiver(IServiceScopeFactory scopes, ILogger<GitHubWebhookReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Hands the delivery to every listener, in registration order.</summary>
    public async Task DispatchAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        _logger.LogDebug(
            "GitHub webhook {Event}/{Action} for {Repository} (delivery {DeliveryId})",
            delivery.EventName,
            delivery.Action ?? "-",
            delivery.RepositoryFullName ?? "-",
            delivery.DeliveryId ?? "-");

        await using var scope = _scopes.CreateAsyncScope();

        foreach (var listener in scope.ServiceProvider.GetServices<IGitHubWebhookListener>())
        {
            try
            {
                await listener.OnDeliveryAsync(delivery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One listener failing must not deny the others their delivery, and must not turn a
                // genuine delivery into a 500 that GitHub will simply send again.
                _logger.LogError(
                    ex,
                    "A GitHub webhook listener ({Listener}) failed handling {Event}",
                    listener.GetType().Name,
                    delivery.EventName);
            }
        }
    }
}
