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

/// <summary>Why the section 20b.3 chain produced no credential.</summary>
/// <remarks>
/// The distinction is the whole of the difference between waiting and failing, and collapsing it is
/// how a request came to sit in <c>refining</c> forever: a caller that reads "nothing resolved" and
/// defers is right for one of these values and wrong for the other two, because only one of them
/// comes back on its own.
/// </remarks>
public enum ModelCredentialUnavailability
{
    /// <summary>A credential resolved. Nothing is wrong.</summary>
    None = 0,

    /// <summary>
    /// Nothing could serve this model at all: no grant, no instance-level key, nothing in the chain.
    /// An operator has to configure something; no amount of waiting produces a credential.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// Every candidate is rate limited, and a provider said when capacity returns. Section 20b.3:
    /// this waits, and it does not fail.
    /// </summary>
    WaitingForCapacity,

    /// <summary>
    /// Candidates exist and none of them can be used: invalid, revoked, expired, or exhausted with no
    /// reset instant to wait for. Section 20b.4 is explicit that this needs a human, not a wait.
    /// </summary>
    NeedsAttention,
}

/// <summary>The outcome of walking the section 20b.3 chain.</summary>
public sealed record ModelCredentialResolution
{
    /// <summary>The credential to use, or <see langword="null"/> if nothing could serve.</summary>
    public ResolvedModelCredential? Credential { get; init; }

    /// <summary>
    /// The earliest <c>exhausted_until</c> across every skipped grant, when nothing resolved.
    /// Section 20b.3: the session goes to <c>Queued</c> showing this as <em>waiting for capacity</em>;
    /// it does not fail.
    /// </summary>
    public DateTimeOffset? WaitingForCapacityUntil { get; init; }

    /// <summary>
    /// <see langword="true"/> when nothing resolved because every candidate was unusable, as
    /// opposed to there being no candidates at all.
    /// </summary>
    public bool AllExhausted { get; init; }

    /// <summary>Why nothing resolved.</summary>
    public ModelCredentialUnavailability Unavailability { get; init; }

    /// <summary>
    /// A sentence for whoever has to fix it, naming the variable or the grant that is missing.
    /// Empty on success. Never contains any part of a credential.
    /// </summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Whether a credential was found.</summary>
    public bool Resolved => Credential is not null;

    /// <summary>
    /// Whether waiting is the right response. True only for
    /// <see cref="ModelCredentialUnavailability.WaitingForCapacity"/> — everything else needs a
    /// person, and deferring on it is the silent stall this classification exists to end.
    /// </summary>
    public bool RecoversOnItsOwn => Unavailability == ModelCredentialUnavailability.WaitingForCapacity;

    /// <summary>A successful resolution.</summary>
    public static ModelCredentialResolution Success(ResolvedModelCredential credential) =>
        new() { Credential = credential };

    /// <summary>A resolution that found nothing usable.</summary>
    /// <param name="until">The earliest reset instant seen, if any.</param>
    /// <param name="anyCandidates">Whether anything at all was skipped.</param>
    /// <param name="model">The model that could not be served, for the explanation.</param>
    public static ModelCredentialResolution Exhausted(
        DateTimeOffset? until,
        bool anyCandidates,
        ModelIdentifier? model = null)
    {
        var unavailability = !anyCandidates
            ? ModelCredentialUnavailability.NotConfigured
            : until is not null
                ? ModelCredentialUnavailability.WaitingForCapacity
                : ModelCredentialUnavailability.NeedsAttention;

        return new ModelCredentialResolution
        {
            WaitingForCapacityUntil = until,
            AllExhausted = anyCandidates,
            Unavailability = unavailability,
            Explanation = Describe(unavailability, until, model),
        };
    }

    /// <summary>
    /// The sentence an operator reads. Names the environment variables that could serve the model and
    /// the alternative of linking a grant, because "no model credential" on its own sends somebody to
    /// the logs to find out which one.
    /// </summary>
    private static string Describe(
        ModelCredentialUnavailability unavailability,
        DateTimeOffset? until,
        ModelIdentifier? model)
    {
        var named = model is null ? "the configured model" : model.Canonical;
        var variables = model is null
            ? [InstanceModelCredentials.OpenRouterVariable]
            : InstanceModelCredentials.VariablesFor(model.Provider);
        var joined = string.Join(" or ", variables);

        return unavailability switch
        {
            ModelCredentialUnavailability.NotConfigured =>
                $"No model credential can serve {named}. Set {joined} on this instance and restart it, "
                + "or link a credential for this organisation under Settings -> Credentials.",

            ModelCredentialUnavailability.NeedsAttention =>
                $"Every model credential that could serve {named} is invalid, revoked, or out of "
                + $"capacity with no reset time. Check {joined} and the credentials listed under "
                + "Settings -> Credentials; this will not clear on its own.",

            ModelCredentialUnavailability.WaitingForCapacity =>
                $"Every model credential that could serve {named} is rate limited. Capacity returns at "
                + $"{until:u}.",

            _ => string.Empty,
        };
    }
}

/// <summary>
/// The instance-level decisions the section 20b.3 chain has to respect.
/// </summary>
/// <remarks>
/// <para>
/// Section 20b.7: pooling a personal subscription so that <em>other people's</em> requests run
/// through it is closer to account sharing than to ordinary use, and consumer plan terms may prohibit
/// it. That is the operator's call, expressed as <c>CHARTER_ALLOW_SHARED_POOL</c>, and it has to be
/// consulted somewhere - a grant that says <c>shared_pool</c> on an instance that does not permit
/// pooling must not serve a stranger's session.
/// </para>
/// <para>
/// Carried on the resolver rather than on <see cref="ModelCredentialQuery"/> deliberately: it is a
/// property of the instance, and putting it on the query would mean every caller had to remember to
/// set it, which is how it came to be ignored in the first place.
/// </para>
/// </remarks>
/// <param name="AllowSharedPool"><c>CHARTER_ALLOW_SHARED_POOL</c>.</param>
public sealed record CredentialPolicy(bool AllowSharedPool)
{
    /// <summary>The section 4.2 default: pooling off.</summary>
    public static CredentialPolicy Default { get; } = new(AllowSharedPool: false);

    /// <summary>Pooling permitted, as an operator who set the variable would have it.</summary>
    public static CredentialPolicy Pooled { get; } = new(AllowSharedPool: true);
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
    private readonly CredentialPolicy _policy;

    /// <summary>Creates a resolver.</summary>
    /// <param name="store">Candidate grants, and the three facts resolution writes back.</param>
    /// <param name="timeProvider">The clock exhaustion windows are measured against.</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="policy">
    /// The instance's section 20b.7 position on pooling. Omitted means
    /// <see cref="CredentialPolicy.Default"/>, which is section 4.2's default and the safe one.
    /// </param>
    public CredentialResolver(
        IModelCredentialStore store,
        TimeProvider timeProvider,
        ILogger<CredentialResolver> logger,
        CredentialPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _policy = policy ?? CredentialPolicy.Default;
    }

    /// <inheritdoc />
    public async Task<ModelCredentialResolution> ResolveAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await _store.GetCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
        var resolution = Resolve(query, candidates, _timeProvider.GetUtcNow(), _policy);

        // Said out loud here as well as at the call site. A caller that mishandles the outcome is
        // exactly how this failure went silent the first time, and one line naming the model and the
        // remedy costs nothing on a path that is about to do no work.
        if (resolution.Unavailability is ModelCredentialUnavailability.NotConfigured
            or ModelCredentialUnavailability.NeedsAttention)
        {
            _logger.LogError(
                "No model credential resolved for {Model}: {Explanation}",
                query.Model.Canonical,
                resolution.Explanation);
        }

        return resolution;
    }

    /// <summary>
    /// The pure half of resolution: given candidates and a clock, pick one. Exposed so the ordering
    /// can be tested without a store.
    /// </summary>
    /// <param name="query">Who is asking, and for which model.</param>
    /// <param name="candidates">Every grant that could conceivably serve, in any order.</param>
    /// <param name="now">The clock.</param>
    /// <param name="policy">
    /// The instance's section 20b.7 position on pooling. Omitted means
    /// <see cref="CredentialPolicy.Default"/>: tier 3 is skipped, because an instance that has not
    /// opted in has not agreed to route one person's request through another person's subscription.
    /// </param>
    public static ModelCredentialResolution Resolve(
        ModelCredentialQuery query,
        IReadOnlyList<ModelCredential> candidates,
        DateTimeOffset now,
        CredentialPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var pooling = (policy ?? CredentialPolicy.Default).AllowSharedPool;

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

            if (IsOverflowUsable(credential, overflow, now))
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
        //
        // Section 20b.7: skipped entirely when the instance has not opted in. A grant marked
        // shared_pool then serves nobody but its owner - which is the point of the switch, and the
        // reason it is not enough to refuse the opt-in at the moment a user offers a credential: an
        // instance whose operator turns pooling off must stop honouring the grants that were already
        // pooled, not just decline new ones.
        if (pooling)
        {
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

        return ModelCredentialResolution.Exhausted(earliestReset, sawSkippedCandidate, query.Model);
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

    /// <summary>
    /// Whether tier 2 can serve: the overflow allowance's own state, plus the one thing it shares
    /// with the subscription.
    /// </summary>
    /// <remarks>
    /// Overflow is spent through the same credential, so an expired access token stops it just as it
    /// stops the primary quota — the reason to reach this tier is that the quota ran out, never that
    /// the token did.
    /// </remarks>
    private static bool IsOverflowUsable(
        ModelCredential credential,
        ModelCredentialOverflow overflow,
        DateTimeOffset now)
    {
        if (IsTokenExpired(credential, now))
        {
            return false;
        }

        return overflow.Status switch
        {
            ModelCredentialStatus.Active => true,
            ModelCredentialStatus.Exhausted => overflow.ExhaustedUntil is { } until && until <= now,
            _ => false,
        };
    }

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
