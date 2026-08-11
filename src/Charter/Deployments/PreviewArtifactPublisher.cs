using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// The reachability dot on section 27.7's hosted preview body.
/// </summary>
/// <remarks>
/// Spelled the way the SPA's union spells it (<c>ClientApp/src/api/types.ts</c>) rather than reusing
/// the API contract enum, so this module does not reach into the API layer. The wire values are
/// pinned by test rather than by hope: the payload written here is read back through the API's own
/// projection, which is the only thing that makes two enums with the same shape safe.
/// </remarks>
public enum PreviewReachability
{
    Checking,
    Reachable,
    Unreachable,
    Unknown,
}

/// <summary>Asks a preview URL whether anything is listening.</summary>
/// <remarks>
/// <para>
/// A dot on a card, not a health check. Anything that answers HTTP counts as reachable; a connection
/// that is refused, a name that does not resolve, a request that times out, and a 5xx from a
/// platform edge in front of a dead container all count as unreachable, because from the requester's
/// side those are the same event — the link does not work.
/// </para>
/// <para>
/// This is also the one place in Charter that makes an outbound request to an address somebody else
/// chose, on a loop, from inside the control plane. It therefore checks the URL against
/// <see cref="PreviewUrlPolicy"/> again before every probe rather than trusting that the value was
/// checked when it was stored — the row may predate the check, and the name may have started
/// resolving somewhere else since. <see cref="PreviewHttpClient"/> checks the address once more at the
/// socket, which is the check that cannot be raced.
/// </para>
/// </remarks>
public sealed class PreviewReachabilityProbe
{
    /// <summary>The named <see cref="HttpClient"/> this probe resolves.</summary>
    public const string HttpClientName = "charter.preview";

    private readonly IHttpClientFactory _clients;
    private readonly DeploymentOptions _options;
    private readonly PreviewUrlPolicy _urls;
    private readonly ILogger<PreviewReachabilityProbe> _logger;

    public PreviewReachabilityProbe(
        IHttpClientFactory clients,
        DeploymentOptions options,
        ILogger<PreviewReachabilityProbe> logger,
        PreviewUrlPolicy? urls = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _urls = urls ?? PreviewUrlPolicy.Default;
        _logger = logger;
    }

    /// <summary>Probes one URL. Never throws.</summary>
    public async Task<PreviewReachability> ProbeAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!_options.ProbeReachability)
        {
            return PreviewReachability.Unknown;
        }

        var verdict = await _urls.ValidateAsync(url, cancellationToken);

        if (!verdict.Allowed || verdict.Url is not { } target)
        {
            // "Unknown" rather than "Unreachable": nothing was asked, so nothing was learned. The
            // publisher refuses this URL outright, so this branch is a second line rather than the
            // one a requester ever sees.
            _logger.LogWarning("Refused to probe a preview url: {Reason}", verdict.Reason);

            return PreviewReachability.Unknown;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeTimeout);

        try
        {
            var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            return (int)response.StatusCode >= 500 ? PreviewReachability.Unreachable : PreviewReachability.Reachable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "A preview URL did not answer a reachability probe");
            return PreviewReachability.Unreachable;
        }
    }
}

/// <summary>What applying a deployment did to the session's artifact.</summary>
/// <param name="Changed">False when the artifact already said this, so nothing was written.</param>
/// <param name="State">The artifact's state afterwards, before the expiry clock is applied.</param>
public sealed record PreviewPublication(bool Changed, VerificationArtifactState State);

/// <summary>
/// Turns a bound deployment into the <c>hosted_preview</c> verification artifact of section 27.1 —
/// the thing a requester actually clicks.
/// </summary>
/// <remarks>
/// <para>
/// One artifact per session, of kind <c>hosted_preview</c>, audience <c>requester</c>. A web preview
/// is requester-facing by definition: section 27.4 puts web and API at the top of the table where the
/// requester clicks a link and gets the full loop, and marking it <c>engineer_only</c> would remove
/// the one project class where Charter's core promise holds completely.
/// </para>
/// <para>
/// <strong>Nothing here touches acceptance criteria.</strong> The card's "What to check" list is
/// rendered from the approved spec verbatim (sections 11, 27.7) by the API projection, which reads
/// the same list instance both spec renderings read. This publisher never copies them, never
/// summarises them, and never writes a second copy that could drift from the contract the requester
/// approved.
/// </para>
/// <para>
/// Applying is idempotent and quiet: a deployment that says what the artifact already says writes
/// nothing at all, which is what makes it safe to run on a loop every few seconds (section 2.3 —
/// state lives in Postgres, not in the process).
/// </para>
/// </remarks>
public sealed class PreviewArtifactPublisher
{
    /// <summary>What the card says above the button when a preview is first published.</summary>
    public const string Instructions =
        "This is a full copy of the app with your change in it. Nothing you do here touches the real one.";

    /// <summary>
    /// camelCase properties and snake_case enum values — the spelling
    /// <c>Charter.Api.CharterApiJson</c> reads the payload back with, and the SPA's union expects.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = CreatePayloadOptions();

    private readonly CharterDbContext _database;
    private readonly DeploymentOptions _options;
    private readonly PreviewReachabilityProbe _probe;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreviewArtifactPublisher> _logger;
    private readonly PreviewReadyAnnouncer? _announcer;
    private readonly PreviewUrlPolicy _urls;

    public PreviewArtifactPublisher(
        CharterDbContext database,
        DeploymentOptions options,
        PreviewReachabilityProbe probe,
        TimeProvider clock,
        ILogger<PreviewArtifactPublisher> logger,
        PreviewReadyAnnouncer? announcer = null,
        PreviewUrlPolicy? urls = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _probe = probe;
        _clock = clock;
        _logger = logger;
        _announcer = announcer;
        _urls = urls ?? PreviewUrlPolicy.Default;
    }

    /// <summary>Applies a deployment to the session's hosted preview artifact, and saves.</summary>
    /// <param name="sessionId">The session the change request belongs to.</param>
    /// <param name="deployment">The bound deployment, as the webhook or a provider reported it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<PreviewPublication> ApplyAsync(
        Guid sessionId,
        Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var now = _clock.GetUtcNow();

        var artifact = await _database.VerificationArtifacts
            .Where(row => row.SessionId == sessionId && row.Kind == VerificationArtifactKind.HostedPreview)
            .OrderBy(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var created = artifact is null;

        if (artifact is null)
        {
            artifact = VerificationArtifact.Pending(
                sessionId,
                VerificationArtifactKind.HostedPreview,
                VerificationArtifactAudience.Requester,
                now);

            _database.VerificationArtifacts.Add(artifact);
        }

        var changed = created;

        var urlRefused = deployment.State == DeploymentState.Ready
                         && !string.IsNullOrWhiteSpace(deployment.Url)
                         && !(await _urls.ValidateAsync(deployment.Url, cancellationToken)).Allowed;

        switch (deployment.State)
        {
            // Section 16.3: validate again at the point of use. The binder refuses an unsafe URL
            // before it is ever written, but a fix at the recording point cannot reach rows already in
            // the database and an upgrade does not rewrite them — and a name that was public when it
            // was recorded can be pointed somewhere else afterwards. This is the last check before a
            // URL becomes a button under the words "Nothing you do here touches the real one".
            case DeploymentState.Ready when urlRefused:
                {
                    _logger.LogWarning(
                        "Withheld the preview url on session {SessionId}: it is not one Charter will show a requester",
                        sessionId);

                    if (artifact.State != VerificationArtifactState.Failed)
                    {
                        artifact.MarkFailed();
                        changed = true;
                    }

                    break;
                }

            case DeploymentState.Ready when !string.IsNullOrWhiteSpace(deployment.Url):
                {
                    var isNewPreview = artifact.State != VerificationArtifactState.Ready
                                       || !string.Equals(artifact.Url, deployment.Url, StringComparison.Ordinal);

                    var reachability = await _probe.ProbeAsync(deployment.Url, cancellationToken);
                    var payload = Payload(deployment.Url, reachability);

                    // The expiry is stamped when a preview becomes ready and never afterwards. Extending
                    // it on every reconcile would make section 27.7's countdown a clock that never runs
                    // down, and the countdown is the mitigation for the confusion expiry causes.
                    var expiresAt = isNewPreview ? _options.ExpiryFor(now) : artifact.ExpiresAt;

                    if (isNewPreview || !SamePayload(artifact.Payload, payload))
                    {
                        artifact.MarkReady(
                            url: deployment.Url,
                            instructionsMd: artifact.InstructionsMd ?? Instructions,
                            expiresAt: expiresAt,
                            payload: payload);

                        changed = true;
                    }

                    break;
                }

            case DeploymentState.Ready:
            case DeploymentState.Pending:
            case DeploymentState.Building:
                {
                    // A rebuild, or a redeploy of the same change request. Section 27.7's pending state is
                    // a skeleton with an elapsed timer — never an ETA — and it must not sit above the URL
                    // of the build being replaced.
                    if (artifact.State != VerificationArtifactState.Pending)
                    {
                        artifact.MarkPending();
                        changed = true;
                    }

                    break;
                }

            case DeploymentState.Failed:
            case DeploymentState.Cancelled:
                {
                    if (artifact.State != VerificationArtifactState.Failed)
                    {
                        artifact.MarkFailed();
                        changed = true;
                    }

                    break;
                }

            case DeploymentState.Expired:
                {
                    if (artifact.State != VerificationArtifactState.Expired)
                    {
                        artifact.MarkExpired();
                        changed = true;
                    }

                    break;
                }

            default:
                break;
        }

        if (changed)
        {
            await _database.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Preview artifact {ArtifactId} for session {SessionId} is {State} (provider {Provider})",
                artifact.Id,
                sessionId,
                artifact.State,
                deployment.Provider);
        }

        // Section 6: the artifact turning Ready is the moment the request reaches PreviewReady, the
        // second and last state that notifies anybody. Run whether or not this call changed
        // anything — the state and the notification are separate facts, and a reconcile that found
        // the artifact already Ready must still finish a transition an earlier one was interrupted
        // part-way through (section 2.3).
        if (artifact.State == VerificationArtifactState.Ready && _announcer is not null)
        {
            await _announcer.AnnounceAsync(sessionId, cancellationToken);
        }

        return new PreviewPublication(changed, artifact.State);
    }

    /// <summary>The <c>hosted_preview</c> body of section 27.7, as stored jsonb.</summary>
    /// <remarks>
    /// <c>displayUrl</c> is deliberately absent: the API truncates the URL for the chip itself, and
    /// writing a second, shorter copy here would be a value that can disagree with the one being
    /// opened.
    /// </remarks>
    public static string Payload(string url, PreviewReachability reachability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        return JsonSerializer.Serialize(
            new StoredHostedPreview { Url = url, Reachability = reachability },
            PayloadOptions);
    }

    /// <summary>
    /// Whether the stored body already says what the fresh one says.
    /// </summary>
    /// <remarks>
    /// Compared as JSON rather than as characters, because the column is <c>jsonb</c>: Postgres parses
    /// what it is given and renders its own spelling back, so <c>{"url":"x"}</c> returns as
    /// <c>{"url": "x"}</c>. A textual comparison therefore never matches once the row has been read
    /// back, and this method runs on a loop every few seconds — the difference between "quiet unless
    /// something changed" and a write, a log line and a save on every single sweep.
    /// </remarks>
    private static bool SamePayload(string? stored, string fresh)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(fresh));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreatePayloadOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    private sealed record StoredHostedPreview
    {
        public required string Url { get; init; }

        public required PreviewReachability Reachability { get; init; }
    }
}
