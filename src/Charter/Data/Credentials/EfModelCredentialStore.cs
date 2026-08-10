using Charter.Domain;
using Charter.Models;
using Charter.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Data.Credentials;

/// <summary>
/// Binds <see cref="IModelCredentialStore"/> to the <see cref="CredentialGrant"/> table.
/// </summary>
/// <remarks>
/// <para>
/// The seam the model layer deliberately left open (section 20b.2): the persistence layer owns the
/// row, the encryption and the lifecycle, and the model layer only ever sees the projection. So this
/// class is where ciphertext becomes a <see cref="ModelSecret"/> and nowhere else does.
/// </para>
/// <para>
/// Ordering is not this class's job. <see cref="CredentialResolver"/> applies the section 20b.3
/// chain itself, so <see cref="GetCandidatesAsync"/> returns everything that could serve the query
/// in whatever order the database produced it.
/// </para>
/// </remarks>
public sealed class EfModelCredentialStore : IModelCredentialStore
{
    private readonly CharterDbContext _db;
    private readonly ICredentialProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfModelCredentialStore> _logger;

    /// <summary>Creates a store.</summary>
    public EfModelCredentialStore(
        CharterDbContext db,
        ICredentialProtector protector,
        TimeProvider timeProvider,
        ILogger<EfModelCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelCredential>> GetCandidatesAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requester = ParseId(query.RequesterUserId);
        var organization = ParseId(query.OrganizationId);

        if (requester is null && organization is null)
        {
            return [];
        }

        // Only grants whose kind can actually authenticate against the model's provider. Filtering
        // here rather than after projection means a grant that could never serve this session is
        // never decrypted - the cheapest way to hold to "decrypt as little as possible".
        var kinds = ServingKinds(query.Model.Provider);
        if (kinds.Count == 0)
        {
            return [];
        }

        var metered = kinds.Where(kind => !IsSubscriptionKind(kind)).ToList();

        var grants = await _db.CredentialGrants
            .AsNoTracking()
            .Where(grant => kinds.Contains(grant.Kind))
            // Revoked grants are excluded rather than projected: revocation is immediate
            // (section 20b.2), and there is nothing to be learned from decrypting a dead secret.
            .Where(grant => grant.Status != CredentialStatus.Revoked)
            .Where(grant =>
                // Tiers 1 and 2: the requester's own grants, personal or pooled.
                (requester != null && grant.OwnerUserId == requester.Value)
                // Tier 3: the organisation's shared pool - grants whose owners opted in explicitly.
                || (organization != null
                    && grant.OrgId == organization.Value
                    && grant.Scope == CredentialScope.SharedPool)
                // Tiers 4 and 5: the organisation's metered keys and OpenRouter. Metered grants are
                // org-level by nature - they bill the organisation's account, and section 20b.5's
                // consent mechanics exist for *subscription* quota, which is the scarce thing one
                // person spends on another's behalf. A subscription belonging to somebody else is
                // therefore reachable only through the shared pool above, never through this arm.
                || (organization != null
                    && grant.OrgId == organization.Value
                    && metered.Contains(grant.Kind)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<ModelCredential>(grants.Count);

        foreach (var grant in grants)
        {
            var candidate = TryProject(grant);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <inheritdoc />
    public async Task MarkExhaustedAsync(
        string credentialId,
        DateTimeOffset? exhaustedUntil,
        bool useOverflow,
        CancellationToken cancellationToken = default)
    {
        var grant = await LoadAsync(credentialId, nameof(MarkExhaustedAsync), cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        if (useOverflow)
        {
            // Tier 2 of section 20b.3 has its own columns, so an exhausted overflow allowance is
            // recorded against the overflow and the subscription's own state is left where it was.
            // Widening it to the whole grant would throw away the distinction the resolver walks on.
            grant.MarkOverflowExhausted(exhaustedUntil);
        }
        else
        {
            grant.MarkExhausted(exhaustedUntil);
        }

        if (exhaustedUntil is null)
        {
            // Null is stored as null: "exhausted, no idea when it comes back". A far-future sentinel
            // would read out as "waiting for capacity until the year 9999" wherever section 20b.3
            // renders the instant.
            _logger.LogWarning(
                "Credential grant {GrantId} was exhausted without a reset header ({Allowance}); it " +
                "stays skipped until it is refreshed or cleared.",
                grant.Id,
                useOverflow ? "overflow allowance" : "primary quota");
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkInvalidAsync(
        string credentialId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var grant = await LoadAsync(credentialId, nameof(MarkInvalidAsync), cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        // The reason is persisted, not just logged: an admin looking at a dead credential in the UI
        // needs to know whether the provider rejected the key or the subscription lapsed, and a log
        // line is not where they are looking. The interface guarantees this is short human-readable
        // prose and never credential material, and the entity truncates it either way.
        grant.MarkInvalid(reason);

        _logger.LogError(
            "Credential grant {GrantId} marked invalid: {Reason}. It needs a human, not a wait.",
            grant.Id,
            reason);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordUsageAsync(
        string credentialId,
        ModelUsage usage,
        ModelCharge charge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(charge);

        var grant = await LoadAsync(credentialId, nameof(RecordUsageAsync), cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        grant.RecordUse(_timeProvider.GetUtcNow());

        // Section 20b.5's owner-visible attribution - who used it, for what, how much - is a
        // LedgerEntry, and a ledger entry needs the session and requester this interface does not
        // carry. What can be recorded here is recorded: last used on the row, both currencies in the
        // log. Nothing below is credential material.
        _logger.LogInformation(
            "Credential grant {GrantId} used: {InputTokens} in, {OutputTokens} out, {Unit} " +
            "{CostUsd} (notional {NotionalCostUsd}), owner {OwnerUserId}.",
            grant.Id,
            usage.TotalInputTokens,
            usage.OutputTokens,
            charge.Unit,
            charge.CostUsd,
            charge.NotionalCostUsd,
            charge.OwnerUserId);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a row onto the model layer's view, decrypting the secret on the way through.
    /// </summary>
    /// <returns>
    /// The projection, or <see langword="null"/> when the secret could not be decrypted - a grant
    /// nothing can read is a grant nothing can use, and failing the whole resolution would take
    /// every other credential down with it.
    /// </returns>
    private ModelCredential? TryProject(CredentialGrant grant)
    {
        ModelSecret secret;

        try
        {
            secret = new ModelSecret(_protector.Unprotect(grant.SecretEncrypted));
        }
        catch (CredentialProtectionException ex)
        {
            // Logged loudly and by id only. Silence here would look exactly like "the user never
            // linked a credential", which is the wrong thing for an operator to go debugging.
            _logger.LogError(
                ex,
                "Credential grant {GrantId} could not be decrypted and is being skipped. Check that " +
                "CHARTER_CREDENTIAL_KEY is the key these grants were encrypted with.",
                grant.Id);

            return null;
        }

        return Project(grant, secret);
    }

    /// <summary>
    /// The pure half of the projection: everything except the decryption. Exposed to the tests so
    /// the enum and priority mappings can be pinned without a database.
    /// </summary>
    internal static ModelCredential Project(CredentialGrant grant, ModelSecret secret)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return new ModelCredential
        {
            Id = grant.Id.ToString(),
            Kind = MapKind(grant.Kind),
            Secret = secret,
            OwnerUserId = grant.OwnerUserId.ToString(),
            OrganizationId = grant.OrgId.ToString(),
            BaseUrl = ParseBaseUrl(grant.BaseUrl),
            Scope = MapScope(grant.Scope),
            Status = MapStatus(grant.Status),
            ExhaustedUntil = grant.ExhaustedUntil,
            // The two types order priority in opposite directions - the entity documents "higher
            // wins", the projection "lower values are tried first" - and the resolver sorts
            // ascending. Negating here is what keeps a grant an operator ranked highest actually
            // first, instead of dead last.
            Priority = grant.Priority == int.MinValue ? int.MaxValue : -grant.Priority,
            MaxSessionsPerDayFromOthers = grant.MaxSessionsPerDayFromOthers,
            // Section 20b.3's tier 2. Null when the owner configured no overflow, which the resolver
            // reads as "skip to the shared pool"; otherwise the allowance's own status and reset,
            // which is what makes the tier reachable at all.
            Overflow = grant.OverflowEnabled
                ? new ModelCredentialOverflow
                {
                    Enabled = true,
                    Status = MapStatus(grant.OverflowStatus),
                    ExhaustedUntil = grant.OverflowExhaustedUntil,
                }
                : null,
            ExpiresAt = grant.ExpiresAt,
        };
    }

    // CS8524 is "you have not handled the unnamed enum values", i.e. the result of casting an
    // arbitrary integer to the enum. The three switches below cover every *named* value and have no
    // default arm on purpose, which is what makes adding a member to CredentialKind, CredentialScope
    // or CredentialStatus a compile error (CS8509) here rather than a grant that silently never
    // resolves. Adding `_ =>` to satisfy CS8524 would throw that guarantee away, so the narrower
    // diagnostic is suppressed and CS8509 is left armed. The column is a constrained enum string, so
    // a value with no name cannot come back out of it.
#pragma warning disable CS8524

    /// <summary>
    /// Maps the persisted kind onto the model layer's. No default arm, deliberately: adding a kind
    /// to <see cref="CredentialKind"/> must be a compile error here rather than a grant that
    /// silently never resolves.
    /// </summary>
    internal static ModelCredentialKind MapKind(CredentialKind kind) => kind switch
    {
        CredentialKind.AnthropicOauth => ModelCredentialKind.AnthropicOAuth,
        CredentialKind.AnthropicApiKey => ModelCredentialKind.AnthropicApiKey,
        CredentialKind.OpenAiOauth => ModelCredentialKind.OpenAiOAuth,
        CredentialKind.OpenAiApiKey => ModelCredentialKind.OpenAiApiKey,
        CredentialKind.GoogleApiKey => ModelCredentialKind.GoogleApiKey,
        CredentialKind.XaiApiKey => ModelCredentialKind.XaiApiKey,
        CredentialKind.OpenRouterKey => ModelCredentialKind.OpenRouterKey,
        CredentialKind.CursorApiKey => ModelCredentialKind.CursorApiKey,
        CredentialKind.CustomOpenAiCompatible => ModelCredentialKind.CustomOpenAiCompatible,
    };

    /// <summary>Maps the persisted scope. No default arm, for the same reason as the kinds.</summary>
    internal static ModelCredentialScope MapScope(CredentialScope scope) => scope switch
    {
        CredentialScope.Personal => ModelCredentialScope.Personal,
        CredentialScope.SharedPool => ModelCredentialScope.SharedPool,
    };

    /// <summary>Maps the persisted status. No default arm, for the same reason as the kinds.</summary>
    internal static ModelCredentialStatus MapStatus(CredentialStatus status) => status switch
    {
        CredentialStatus.Active => ModelCredentialStatus.Active,
        CredentialStatus.Exhausted => ModelCredentialStatus.Exhausted,
        CredentialStatus.Invalid => ModelCredentialStatus.Invalid,
        CredentialStatus.Revoked => ModelCredentialStatus.Revoked,
    };

#pragma warning restore CS8524

    /// <summary>Whether a kind is subscription-backed, and so spends quota rather than dollars.</summary>
    internal static bool IsSubscriptionKind(CredentialKind kind)
        => kind is CredentialKind.AnthropicOauth or CredentialKind.OpenAiOauth;

    /// <summary>
    /// The kinds that can authenticate against <paramref name="provider"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors the resolver's own eligibility rule so the store does not hand it candidates it will
    /// discard: an <c>openrouter/</c>-qualified model can only be served by an OpenRouter key, an
    /// OpenRouter key can serve anything else, and a custom OpenAI-compatible endpoint covers the
    /// providers that speak <c>/chat/completions</c> through a configured base URL.
    /// <para>
    /// <see cref="CredentialKind.CursorApiKey"/> is deliberately absent from every arm. It buys an
    /// agent run through the <c>cursor-agent</c> CLI, not a control-plane call — there is no Cursor
    /// <c>IModelClient</c> — so offering it as a candidate here would resolve a refinement or recap
    /// call onto a key that cannot make one.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<CredentialKind> ServingKinds(ModelProvider provider)
    {
        if (provider == ModelProvider.OpenRouter)
        {
            return [CredentialKind.OpenRouterKey];
        }

        List<CredentialKind> kinds = [CredentialKind.OpenRouterKey];

        switch (provider)
        {
            case ModelProvider.Anthropic:
                kinds.Add(CredentialKind.AnthropicOauth);
                kinds.Add(CredentialKind.AnthropicApiKey);
                break;

            case ModelProvider.OpenAi:
                kinds.Add(CredentialKind.OpenAiOauth);
                kinds.Add(CredentialKind.OpenAiApiKey);
                break;

            case ModelProvider.Google:
                kinds.Add(CredentialKind.GoogleApiKey);
                break;

            case ModelProvider.XAi:
                kinds.Add(CredentialKind.XaiApiKey);
                kinds.Add(CredentialKind.CustomOpenAiCompatible);
                break;

            case ModelProvider.DeepSeek:
            case ModelProvider.Groq:
            case ModelProvider.AzureOpenAi:
            case ModelProvider.Ollama:
            case ModelProvider.OpenAiCompatible:
                kinds.Add(CredentialKind.CustomOpenAiCompatible);
                break;

            case ModelProvider.OpenRouter:
            default:
                break;
        }

        return kinds;
    }

    private static Uri? ParseBaseUrl(string? baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri : null;

    private static Guid? ParseId(string? id)
        => Guid.TryParse(id, out var parsed) ? parsed : null;

    private async Task<CredentialGrant?> LoadAsync(
        string credentialId,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        var id = ParseId(credentialId);
        var grant = id is null
            ? null
            : await _db.CredentialGrants
                .FirstOrDefaultAsync(candidate => candidate.Id == id.Value, cancellationToken)
                .ConfigureAwait(false);

        if (grant is null)
        {
            // A grant deleted between resolution and the write back. Worth a line, not an exception:
            // this runs on the failure path of a session that has already gone wrong.
            _logger.LogWarning(
                "{Operation} found no credential grant {GrantId}; it was deleted or the id is not one.",
                operation,
                credentialId);
        }

        return grant;
    }
}
