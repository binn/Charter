namespace Charter.Models;

/// <summary>Which session a credential is being resolved for.</summary>
/// <param name="Model">The model the session will run, which constrains which grants can serve it.</param>
/// <param name="RequesterUserId">The person whose request this is. Tier 1 of section 20b.3.</param>
/// <param name="OrganizationId">The organisation whose pool and metered keys are in play.</param>
public sealed record ModelCredentialQuery(
    ModelIdentifier Model,
    string? RequesterUserId,
    string? OrganizationId);

/// <summary>
/// The model layer's view of credential storage.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small, and deliberately not EF. The resolver needs to read candidate grants and to
/// write back three facts - exhausted, invalid, last used - and nothing else. The orchestrator binds
/// this to the <c>CredentialGrant</c> entity and owns decryption, revocation and the audit trail.
/// </para>
/// <para>
/// Implementations must already have filtered out revoked grants, or return them with
/// <see cref="ModelCredentialStatus.Revoked"/> so the resolver can skip them.
/// </para>
/// </remarks>
public interface IModelCredentialStore
{
    /// <summary>
    /// Returns every grant that could conceivably serve the query, in any order. The resolver
    /// applies the section 20b.3 ordering itself; the store does not need to.
    /// </summary>
    Task<IReadOnlyList<ModelCredential>> GetCandidatesAsync(
        ModelCredentialQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a grant exhausted and records when it resets. Section 20b.4: a <c>429</c> exhausts the
    /// grant rather than triggering a blind retry.
    /// </summary>
    /// <param name="credentialId">The grant.</param>
    /// <param name="exhaustedUntil">
    /// The reset instant taken from the provider's reset header, or <see langword="null"/> when the
    /// provider did not say - in which case the grant stays exhausted until something clears it.
    /// </param>
    /// <param name="useOverflow">
    /// Whether it was the grant's overflow allowance that was exhausted rather than its primary
    /// subscription quota.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task MarkExhaustedAsync(
        string credentialId,
        DateTimeOffset? exhaustedUntil,
        bool useOverflow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a grant invalid after a hard authentication failure. Distinct from exhaustion: this one
    /// needs a human, not a wait.
    /// </summary>
    /// <param name="credentialId">The grant.</param>
    /// <param name="reason">
    /// A short, human-readable reason. Implementations must not be handed - and this layer never
    /// passes - any credential material here.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task MarkInvalidAsync(
        string credentialId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Records a successful use, for the owner-visible attribution of section 20b.5.</summary>
    Task RecordUsageAsync(
        string credentialId,
        ModelUsage usage,
        ModelCharge charge,
        CancellationToken cancellationToken = default);
}
