using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>
/// Adds the section 4.2 instance-level keys to whatever a stored-grant credential store returns.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a change to the EF store, because the two sources have nothing in common
/// below the interface: one decrypts rows and writes columns, the other reads two environment
/// variables that were validated at startup. Composing them here keeps
/// <c>EfModelCredentialStore</c> about <c>credential_grants</c> and keeps
/// <see cref="CredentialResolver"/> unaware that a credential can come from anywhere but a store —
/// the section 20b.3 ordering applies to instance keys because they are ordinary candidates of an
/// ordinary kind, not because the chain grew a case for them.
/// </para>
/// <para>
/// The write-backs are routed by id. An instance-level id is not a <see cref="Guid"/>, so forwarding
/// one to the EF store would find no row and log "the grant was deleted", which is both untrue and
/// the wrong thing to send an operator looking for. Section 20b.4's exhaustion and invalidity are
/// recorded against the variable instead, in <see cref="InstanceModelCredentials"/>.
/// </para>
/// </remarks>
public sealed class InstanceKeyModelCredentialStore : IModelCredentialStore
{
    private readonly IModelCredentialStore _inner;
    private readonly InstanceModelCredentials _instance;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstanceKeyModelCredentialStore> _logger;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The stored-grant store this wraps.</param>
    /// <param name="instance">The instance-level keys.</param>
    /// <param name="timeProvider">The clock last-used is stamped from.</param>
    /// <param name="logger">Where instance-level failures are recorded, by variable and never by value.</param>
    public InstanceKeyModelCredentialStore(
        IModelCredentialStore inner,
        InstanceModelCredentials instance,
        TimeProvider timeProvider,
        ILogger<InstanceKeyModelCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _instance = instance;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelCredential>> GetCandidatesAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var stored = await _inner.GetCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
        var instance = _instance.Candidates();

        if (instance.Count == 0)
        {
            return stored;
        }

        // Appended, not prepended: ordering is the resolver's job and priority is what expresses
        // "fallback" within a tier. The order this list arrives in is irrelevant by contract.
        var candidates = new List<ModelCredential>(stored.Count + instance.Count);
        candidates.AddRange(stored);
        candidates.AddRange(instance);

        return candidates;
    }

    /// <inheritdoc />
    public Task MarkExhaustedAsync(
        string credentialId,
        DateTimeOffset? exhaustedUntil,
        bool useOverflow,
        CancellationToken cancellationToken = default)
    {
        if (!_instance.Owns(credentialId))
        {
            return _inner.MarkExhaustedAsync(credentialId, exhaustedUntil, useOverflow, cancellationToken);
        }

        _instance.MarkExhausted(credentialId, exhaustedUntil);

        _logger.LogWarning(
            "The instance-level model credential from {Variable} was rate limited; capacity returns "
            + "{ExhaustedUntil}. Sessions fall through to the next credential in the section 20b.3 chain "
            + "until then.",
            _instance.VariableFor(credentialId),
            exhaustedUntil);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkInvalidAsync(
        string credentialId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!_instance.Owns(credentialId))
        {
            return _inner.MarkInvalidAsync(credentialId, reason, cancellationToken);
        }

        var variable = _instance.VariableFor(credentialId);
        _instance.MarkInvalid(credentialId, reason);

        // Loud, and at Error: this is the one credential failure an operator can fix in seconds, and
        // the only thing standing between them and fixing it is being told. The variable is named;
        // its value never is.
        _logger.LogError(
            "The instance-level model credential from {Variable} was rejected by the provider: {Reason}. "
            + "Charter will not present it again until the key is changed and the instance restarted.",
            variable,
            reason);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordUsageAsync(
        string credentialId,
        ModelUsage usage,
        ModelCharge charge,
        CancellationToken cancellationToken = default)
    {
        if (!_instance.Owns(credentialId))
        {
            return _inner.RecordUsageAsync(credentialId, usage, charge, cancellationToken);
        }

        _instance.RecordUse(credentialId, _timeProvider.GetUtcNow());

        return Task.CompletedTask;
    }
}
