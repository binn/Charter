using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>The runner backends of section 2.2, selected by <c>CHARTER_RUNNER</c>.</summary>
public enum RunnerKind
{
    /// <summary>The Charter Agent daemon on the operator's own hardware (section 33).</summary>
    Agent,

    /// <summary><c>repository_dispatch</c> into a workflow. The zero-infrastructure PaaS default.</summary>
    [EnumMember(Value = "github-actions")]
    GitHubActions,

    /// <summary>Sibling containers over the local Docker socket. Root-equivalent host access.</summary>
    Docker,
}

/// <summary>The lifecycle of one agent run, as section 6 draws it.</summary>
public enum SessionStatus
{
    Queued,

    Running,

    NeedsInput,

    PrOpen,

    PreviewReady,

    InReview,

    Merged,

    Failed,

    Cancelled,

    Stale,

    /// <summary>
    /// An engineer took the branch over (section 7.5). Charter stops touching it: an agent and a
    /// human editing the same branch concurrently is the one genuinely destructive failure mode in
    /// this design.
    /// </summary>
    HandedOff,
}

/// <summary>One agent run against one <see cref="Spec"/> (section 5).</summary>
/// <remarks>
/// Nothing about a session lives in memory. The container can restart mid-session, so every session
/// must be fully resumable from Postgres alone (section 2.3) — which is why status, cost and the
/// cancel request are all columns rather than orchestrator state.
/// </remarks>
public sealed class Session : IVersionedEntity
{
    private Session()
    {
    }

    private Session(
        Guid id,
        Guid specId,
        RunnerKind runner,
        string agentModel,
        string? baseCommitSha,
        bool autoDispatched,
        DateTimeOffset createdAt)
    {
        Id = id;
        SpecId = specId;
        Runner = runner;
        AgentModel = agentModel;
        BaseCommitSha = baseCommitSha;
        AutoDispatched = autoDispatched;
        Status = SessionStatus.Queued;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SpecId { get; private set; }

    public RunnerKind Runner { get; private set; }

    /// <summary>A provider-qualified identifier such as <c>anthropic/claude-opus-5</c> (section 20b.1).</summary>
    public string AgentModel { get; private set; } = string.Empty;

    /// <summary>Recorded per session so section 17 can tell which open pull requests went stale.</summary>
    public string? BaseCommitSha { get; private set; }

    public SessionStatus Status { get; private set; }

    /// <summary>
    /// True when the spec reached <c>Queued</c> without a human approving it (section 7.5). The pull
    /// request is labelled <c>unreviewed-spec</c> and the engineer recap leads with the fact.
    /// </summary>
    public bool AutoDispatched { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// Real marginal cost. A subscription-backed session costs nothing here and still consumes
    /// quota; see <see cref="LedgerEntry"/>, which carries both units.
    /// </summary>
    public decimal CostUsd { get; private set; }

    /// <summary>Set by the cancel button. The runner must actually die and the cost must settle.</summary>
    public DateTimeOffset? CancelRequestedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public int Version { get; private set; }

    public bool IsTerminal => Status is SessionStatus.Merged
        or SessionStatus.Failed
        or SessionStatus.Cancelled
        or SessionStatus.Stale
        or SessionStatus.HandedOff;

    public static Session Queue(
        Guid specId,
        RunnerKind runner,
        string agentModel,
        string? baseCommitSha = null,
        bool autoDispatched = false,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentModel);

        return new Session(
            id ?? Guid.CreateVersion7(),
            specId,
            runner,
            agentModel.Trim(),
            baseCommitSha,
            autoDispatched,
            DomainTime.Resolve(now));
    }

    public void Start(string baseCommitSha, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommitSha);

        BaseCommitSha = baseCommitSha;
        Status = SessionStatus.Running;
        StartedAt = DomainTime.Resolve(now);
    }

    public void TransitionTo(SessionStatus status, DateTimeOffset? now = null)
    {
        Status = status;
        if (status is SessionStatus.Merged or SessionStatus.Failed or SessionStatus.Cancelled or SessionStatus.Stale)
        {
            EndedAt = DomainTime.Resolve(now);
        }
    }

    /// <summary>Section 11: the cancel button must kill the runner and settle token cost.</summary>
    public void RequestCancellation(DateTimeOffset? now = null)
        => CancelRequestedAt ??= DomainTime.Resolve(now);

    /// <summary>
    /// Section 7.5: taking over is explicit and stops agent writes. Terminal, and deliberately not
    /// reversible.
    /// </summary>
    public void HandOff(DateTimeOffset? now = null)
    {
        Status = SessionStatus.HandedOff;
        EndedAt = DomainTime.Resolve(now);
    }

    /// <summary>Settles observed cost. Cost only ever accumulates; a session never gets cheaper.</summary>
    public void AddCost(decimal costUsd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(costUsd);
        CostUsd += costUsd;
    }

    void IVersionedEntity.NextVersion() => Version++;
}
