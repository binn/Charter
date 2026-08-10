namespace Charter.Domain;

/// <summary>
/// Deployment states as reported by whichever provider is hosting the preview. Charter is
/// provider-agnostic by design (section 18), so this is the common denominator rather than any one
/// platform's vocabulary.
/// </summary>
public enum DeploymentState
{
    Pending,

    Building,

    Ready,

    Failed,

    Cancelled,

    Expired,
}

/// <summary>
/// A preview environment bound to a change request (section 5, as amended by change spec 001 part
/// A.2), ingested either from the generic webhook <c>POST /api/deployments/{prSha}</c> or from
/// change request comment parsing (section 18).
/// </summary>
public sealed class Deployment
{
    private Deployment()
    {
    }

    private Deployment(
        Guid id,
        Guid changeRequestId,
        string provider,
        string? url,
        DeploymentState state,
        DateTimeOffset reportedAt)
    {
        Id = id;
        ChangeRequestId = changeRequestId;
        Provider = provider;
        Url = url;
        State = state;
        ReportedAt = reportedAt;
    }

    public Guid Id { get; private set; }

    public Guid ChangeRequestId { get; private set; }

    /// <summary>Free text: <c>railway</c>, <c>render</c>, <c>fly</c>, <c>coolify</c>, anything.</summary>
    public string Provider { get; private set; } = string.Empty;

    public string? Url { get; private set; }

    public DeploymentState State { get; private set; }

    public DateTimeOffset ReportedAt { get; private set; }

    public static Deployment Report(
        Guid changeRequestId,
        string provider,
        DeploymentState state,
        string? url = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (state == DeploymentState.Ready && string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A ready deployment must carry a URL.", nameof(url));
        }

        return new Deployment(
            id ?? Guid.CreateVersion7(),
            changeRequestId,
            provider.Trim().ToLowerInvariant(),
            url?.Trim(),
            state,
            DomainTime.Resolve(now));
    }

    public void Update(DeploymentState state, string? url = null, DateTimeOffset? now = null)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(url))
        {
            Url = url.Trim();
        }

        ReportedAt = DomainTime.Resolve(now);
    }
}
