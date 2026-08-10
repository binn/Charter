using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>Where in the section 20b.3 chain a credential was found.</summary>
public enum ModelCredentialTier
{
    /// <summary>1. The requester's own linked subscription credential.</summary>
    RequesterSubscription = 1,

    /// <summary>2. Remaining extra or overflow usage on that same credential.</summary>
    RequesterOverflow = 2,

    /// <summary>3. The organisation's shared pool, by priority.</summary>
    OrganizationSharedPool = 3,

    /// <summary>4. The organisation's metered API key.</summary>
    OrganizationMeteredKey = 4,

    /// <summary>5. OpenRouter.</summary>
    OpenRouter = 5,
}

/// <summary>A credential picked out of the chain, with the tier it came from.</summary>
/// <param name="Credential">The grant.</param>
/// <param name="Tier">Where in the chain it was found.</param>
/// <param name="UseOverflow">
/// Whether the grant's overflow allowance is being spent rather than its primary subscription quota.
/// </param>
public sealed record ResolvedModelCredential(
    ModelCredential Credential,
    ModelCredentialTier Tier,
    bool UseOverflow = false);

/// <summary>The outcome of walking the section 20b.3 chain.</summary>
public sealed record ModelCredentialResolution
{
    /// <summary>The credential to use, or <see langword="null"/> if everything was exhausted.</summary>
    public ResolvedModelCredential? Credential { get; init; }

    /// <summary>
    /// The earliest <c>exhausted_until</c> across every skipped grant, when nothing resolved.
    /// Section 20b.3: the session goes to <c>Queued</c> showing this as <em>waiting for capacity</em>;
    /// it does not fail.
    /// </summary>
    public DateTimeOffset? WaitingForCapacityUntil { get; init; }

    /// <summary>
    /// <see langword="true"/> when nothing resolved because every candidate was exhausted, as
    /// opposed to there being no candidates at all.
    /// </summary>
    public bool AllExhausted { get; init; }

    /// <summary>Whether a credential was found.</summary>
    public bool Resolved => Credential is not null;

    /// <summary>A successful resolution.</summary>
    public static ModelCredentialResolution Success(ResolvedModelCredential credential) =>
        new() { Credential = credential };

    /// <summary>A resolution that found nothing usable.</summary>
    public static ModelCredentialResolution Exhausted(DateTimeOffset? until, bool anyCandidates) =>
        new() { WaitingForCapacityUntil = until, AllExhausted = anyCandidates };
}

/// <summary>Walks the section 20b.3 chain and records the outcome of using what it picked.</summary>
public interface ICredentialResolver
{
    /// <summary>Resolves a credential for a session.</summary>
    Task<ModelCredentialResolution> ResolveAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a provider failure against the grant that caused it. A <c>429</c> exhausts the grant
    /// and stores <c>exhausted_until</c>; an authentication failure marks it invalid. Section 20b.4.
    /// </summary>
    Task ReportFailureAsync(
        ResolvedModelCredential credential,
        ModelClientException failure,
        CancellationToken cancellationToken = default);

    /// <summary>Records a successful call for owner-visible attribution. Section 20b.5.</summary>
    Task ReportSuccessAsync(
        ResolvedModelCredential credential,
        ModelCompletion completion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The section 20b.3 resolution chain: requester's own credential, then its overflow, then the org
/// shared pool by priority, then the org metered key, then OpenRouter - skipping anything
/// <c>exhausted</c> or <c>invalid</c>.
/// </summary>
public sealed class CredentialResolver : ICredentialResolver
{
    private readonly IModelCredentialStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CredentialResolver> _logger;

    /// <summary>Creates a resolver.</summary>
    public CredentialResolver(
        IModelCredentialStore store,
        TimeProvider timeProvider,
        ILogger<CredentialResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ModelCredentialResolution> ResolveAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await _store.GetCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
        return Resolve(query, candidates, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The pure half of resolution: given candidates and a clock, pick one. Exposed so the ordering
    /// can be tested without a store.
    /// </summary>
    public static ModelCredentialResolution Resolve(
        ModelCredentialQuery query,
        IReadOnlyList<ModelCredential> candidates,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        DateTimeOffset? earliestReset = null;
        var sawSkippedCandidate = false;

        void NoteSkipped(DateTimeOffset? until)
        {
            sawSkippedCandidate = true;
            if (until is not null && (earliestReset is null || until < earliestReset))
            {
                earliestReset = until;
            }
        }

        var eligible = new List<ModelCredential>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!CanServe(candidate, query.Model))
            {
                continue;
            }

            if (candidate.Status is ModelCredentialStatus.Revoked or ModelCredentialStatus.Invalid)
            {
                sawSkippedCandidate = true;
                continue;
            }

            eligible.Add(candidate);
        }

        // Tier 1 and 2: the requester's own subscription, then its overflow allowance.
        var own = eligible
            .Where(c => c.IsSubscription
                && query.RequesterUserId is not null
                && string.Equals(c.OwnerUserId, query.RequesterUserId, StringComparison.Ordinal))
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var credential in own)
        {
            if (IsUsable(credential, now))
            {
                return ModelCredentialResolution.Success(
                    new ResolvedModelCredential(credential, ModelCredentialTier.RequesterSubscription));
            }

            NoteSkipped(credential.ExhaustedUntil);
        }

        foreach (var credential in own)
        {
            var overflow = credential.Overflow;
            if (overflow is null || !overflow.Enabled)
            {
                continue;
            }

            if (IsOverflowUsable(overflow, now))
            {
                return ModelCredentialResolution.Success(
                    new ResolvedModelCredential(
                        credential,
                        ModelCredentialTier.RequesterOverflow,
                        UseOverflow: true));
            }

            NoteSkipped(overflow.ExhaustedUntil);
        }

        // Tier 3: the organisation's shared pool, by priority. Grants the requester owns were already
        // considered above, so they do not get a second turn here.
        var pool = eligible
            .Where(c => c.Scope == ModelCredentialScope.SharedPool
                && !string.Equals(c.OwnerUserId, query.RequesterUserId, StringComparison.Ordinal))
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Id, StringComparer.Ordinal);

        foreach (var credential in pool)
        {
            if (IsUsable(credential, now))
            {
                return ModelCredentialResolution.Success(
                    new ResolvedModelCredential(credential, ModelCredentialTier.OrganizationSharedPool));
            }

            NoteSkipped(credential.ExhaustedUntil);
        }

        // Tier 4: the organisation's metered API key. Never OpenRouter - that is tier 5.
        var metered = eligible
            .Where(c => !c.IsSubscription
                && c.Kind != ModelCredentialKind.OpenRouterKey
                && c.Scope != ModelCredentialScope.SharedPool)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Id, StringComparer.Ordinal);

        foreach (var credential in metered)
        {
            if (IsUsable(credential, now))
            {
                return ModelCredentialResolution.Success(
                    new ResolvedModelCredential(credential, ModelCredentialTier.OrganizationMeteredKey));
            }

            NoteSkipped(credential.ExhaustedUntil);
        }

        // Tier 5: OpenRouter.
        var openRouter = eligible
            .Where(c => c.Kind == ModelCredentialKind.OpenRouterKey)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Id, StringComparer.Ordinal);

        foreach (var credential in openRouter)
        {
            if (IsUsable(credential, now))
            {
                return ModelCredentialResolution.Success(
                    new ResolvedModelCredential(credential, ModelCredentialTier.OpenRouter));
            }

            NoteSkipped(credential.ExhaustedUntil);
        }

        return ModelCredentialResolution.Exhausted(earliestReset, sawSkippedCandidate);
    }

    /// <inheritdoc />
    public async Task ReportFailureAsync(
        ResolvedModelCredential credential,
        ModelClientException failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(failure);

        switch (failure)
        {
            case ModelRateLimitException rateLimited:
                // Section 20b.4: mark exhausted and record the reset. Never blind-retry.
                _logger.LogWarning(
                    "Credential {CredentialId} exhausted by provider {Provider}; capacity returns {ExhaustedUntil}.",
                    credential.Credential.Id,
                    credential.Credential.Provider,
                    rateLimited.ExhaustedUntil);
                await _store.MarkExhaustedAsync(
                        credential.Credential.Id,
                        rateLimited.ExhaustedUntil,
                        credential.UseOverflow,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ModelAuthenticationException authentication:
                // A hard auth failure is not a rate limit: waiting will not fix it.
                _logger.LogError(
                    "Credential {CredentialId} rejected by provider {Provider} with {StatusCode}; marking invalid.",
                    credential.Credential.Id,
                    credential.Credential.Provider,
                    authentication.StatusCode);
                await _store.MarkInvalidAsync(
                        credential.Credential.Id,
                        $"Provider rejected the credential with {authentication.StatusCode}.",
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning(
                    "Model call failed on credential {CredentialId} against provider {Provider} with {StatusCode}.",
                    credential.Credential.Id,
                    credential.Credential.Provider,
                    failure.StatusCode);
                break;
        }
    }

    /// <inheritdoc />
    public Task ReportSuccessAsync(
        ResolvedModelCredential credential,
        ModelCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(completion);

        return _store.RecordUsageAsync(
            credential.Credential.Id,
            completion.Usage,
            completion.Charge,
            cancellationToken);
    }

    private static bool IsUsable(ModelCredential credential, DateTimeOffset now) =>
        credential.Status switch
        {
            ModelCredentialStatus.Active => !IsTokenExpired(credential, now),
            // A recorded reset that has already passed means the grant recovered on its own.
            ModelCredentialStatus.Exhausted =>
                credential.ExhaustedUntil is { } until && until <= now && !IsTokenExpired(credential, now),
            _ => false,
        };

    private static bool IsOverflowUsable(ModelCredentialOverflow overflow, DateTimeOffset now) =>
        overflow.Status switch
        {
            ModelCredentialStatus.Active => true,
            ModelCredentialStatus.Exhausted => overflow.ExhaustedUntil is { } until && until <= now,
            _ => false,
        };

    private static bool IsTokenExpired(ModelCredential credential, DateTimeOffset now) =>
        credential.ExpiresAt is { } expiry && expiry <= now;

    private static bool CanServe(ModelCredential credential, ModelIdentifier model)
    {
        // OpenRouter is the universal fallback: it can serve any model identifier routed to it, and
        // an openrouter/-qualified model can only be served by an OpenRouter key.
        if (model.Provider == ModelProvider.OpenRouter)
        {
            return credential.Kind == ModelCredentialKind.OpenRouterKey;
        }

        if (credential.Kind == ModelCredentialKind.OpenRouterKey)
        {
            return true;
        }

        return credential.Provider == model.Provider
            || (credential.Kind == ModelCredentialKind.CustomOpenAiCompatible
                && model.Provider is ModelProvider.OpenAiCompatible
                    or ModelProvider.Ollama
                    or ModelProvider.Groq
                    or ModelProvider.DeepSeek
                    or ModelProvider.AzureOpenAi
                    or ModelProvider.XAi);
    }
}
