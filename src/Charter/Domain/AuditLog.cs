namespace Charter.Domain;

/// <summary>
/// Every agent action attributable to a named human (section 7.3, guardrail 5). The agent never acts
/// on its own initiative — no schedulers, no infinite auto-retry — so an entry with no actor is a
/// deliberate system action, not an unattributed one.
/// </summary>
public sealed class AuditLog
{
    private AuditLog()
    {
    }

    private AuditLog(
        Guid id,
        Guid orgId,
        Guid? actorUserId,
        string action,
        string targetType,
        string? targetId,
        string? metadata,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        ActorUserId = actorUserId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        Metadata = metadata;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    /// <summary>Null only for actions Charter itself takes, such as lease expiry or retention pruning.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>A dotted verb such as <c>repo.scope.granted</c> or <c>budget.override.started</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    /// <summary>jsonb. Metadata only — never transcript bodies (section 19).</summary>
    public string? Metadata { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Record(
        Guid orgId,
        string action,
        string targetType,
        Guid? actorUserId = null,
        string? targetId = null,
        string? metadata = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);

        return new AuditLog(
            id ?? Guid.CreateVersion7(),
            orgId,
            actorUserId,
            action.Trim(),
            targetType.Trim(),
            targetId,
            metadata,
            DomainTime.Resolve(now));
    }
}
