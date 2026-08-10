using System.Text.Json;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;

namespace Charter.Auth.Audit;

/// <summary>
/// One thing that happened, attributable to one named human (section 7.3, guardrail 5).
/// </summary>
/// <remarks>
/// <see cref="Metadata"/> is metadata only. Section 19 forbids transcript bodies, prompts and model
/// output in the audit log; the entry says what changed and about which row, and the transcript
/// lives in <c>events</c> where retention applies to it.
/// </remarks>
public sealed record AuditEntry
{
    public required Guid OrgId { get; init; }

    /// <summary>Null only for something Charter itself did, such as lease expiry (section 7.3).</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>One of <see cref="AuditActions"/>.</summary>
    public required string Action { get; init; }

    public required string TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>Small, flat, and never secret-bearing.</summary>
    public IReadOnlyDictionary<string, string?>? Metadata { get; init; }
}

/// <summary>The write path for the audit log.</summary>
public interface IAuditWriter
{
    /// <summary>Appends an entry. Enlists in the caller's transaction when there is one.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends the entry an authorisation grant already labelled itself with, and does nothing when
    /// the decision was routine or refused.
    /// </summary>
    /// <remarks>
    /// This is the "hard to forget" half of guardrail 5: a policy that decides something notable
    /// puts the verb on the <see cref="AuthorizationDecision"/>, and every caller that acts on a
    /// decision can hand the whole decision here without knowing which verb it was.
    /// </remarks>
    Task RecordGrantAsync(
        MemberSnapshot member,
        AuthorizationDecision decision,
        string targetType,
        string? targetId = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Writes audit entries through <see cref="CharterDbContext"/>.</summary>
/// <remarks>
/// Entries are added and saved immediately when the caller has no ambient transaction, and join the
/// caller's transaction when it does — so "the thing happened" and "the thing was recorded" commit
/// or roll back together rather than drifting.
/// </remarks>
public sealed class AuditWriter : IAuditWriter
{
    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = false };

    private readonly CharterDbContext database;
    private readonly TimeProvider clock;

    public AuditWriter(CharterDbContext database, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        database.AuditLogs.Add(ToRow(entry, clock.GetUtcNow()));

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordGrantAsync(
        MemberSnapshot member,
        AuthorizationDecision decision,
        string targetType,
        string? targetId = null,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);

        if (decision.AuditAction is not { } action)
        {
            return Task.CompletedTask;
        }

        return RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = member.UserId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Metadata = metadata,
            },
            cancellationToken);
    }

    /// <summary>Builds the row without saving, for a caller composing one transaction by hand.</summary>
    public static AuditLog ToRow(AuditEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return AuditLog.Record(
            entry.OrgId,
            entry.Action,
            entry.TargetType,
            entry.ActorUserId,
            entry.TargetId,
            entry.Metadata is null or { Count: 0 } ? null : JsonSerializer.Serialize(entry.Metadata, MetadataJson),
            now);
    }
}
