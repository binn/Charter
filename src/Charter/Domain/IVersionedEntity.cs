namespace Charter.Domain;

/// <summary>
/// An entity whose row is written by more than one code path and therefore carries an optimistic
/// concurrency token.
/// </summary>
/// <remarks>
/// <see cref="Session"/> is written by the orchestrator, by webhooks and by a cancel request;
/// <see cref="Job"/> is written by the dispatcher, by the claiming worker and by lease expiry. The
/// token is a plain integer column rather than <c>xmin</c>: Npgsql 10 removed
/// <c>UseXminAsConcurrencyToken</c>, and a system column cannot be expressed in a migration.
/// <see cref="Charter.Data.CharterDbContext"/> increments it on save, and the raw SQL in
/// <see cref="Charter.Data.JobQueue"/> increments it in the same statement it mutates the row.
/// </remarks>
internal interface IVersionedEntity
{
    int Version { get; }

    void NextVersion();
}
