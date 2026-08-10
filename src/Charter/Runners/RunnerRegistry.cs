using Charter.Domain;

namespace Charter.Runners;

/// <summary>The outcome of matching one session's requirements against the registered runners.</summary>
/// <param name="Runner">The backend to dispatch to, or <see langword="null"/> when none is eligible.</param>
/// <param name="Explanation">
/// Why nothing matched, in the shape section 27.3 gives:
/// <em>No runner available with macOS and Xcode 16. Register one in Settings → Runners.</em> Null on
/// a successful match.
/// </param>
/// <param name="Missing">The requirements no registered runner covers.</param>
public sealed record RunnerRouting(IAgentRunner? Runner, string? Explanation, IReadOnlyList<string> Missing)
{
    /// <summary>True when a backend was found. False means <em>queue</em>, never <em>fail</em>.</summary>
    public bool IsRoutable => Runner is not null;
}

/// <summary>Every backend this instance may dispatch to, and the capability match of section 27.3.</summary>
public interface IRunnerRegistry
{
    /// <summary>The enabled backends, in <c>CHARTER_RUNNER</c> order.</summary>
    IReadOnlyList<IAgentRunner> Runners { get; }

    /// <summary>
    /// The union of everything every online runner advertises, expanded.
    /// </summary>
    /// <remarks>
    /// Handed to <see cref="Charter.Data.JobQueue.ClaimAsync"/> as the dispatcher's capability set, so
    /// a job this instance could not run is never claimed in the first place. That is what makes "a
    /// session with no eligible runner queues" the default behaviour of the queue rather than a
    /// branch somebody has to remember to write: an unclaimable job simply stays pending.
    /// </remarks>
    ValueTask<IReadOnlyList<string>> AdvertisedCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Picks the backend for a session, or explains why there is not one (section 27.3).</summary>
    ValueTask<RunnerRouting> RouteAsync(
        IReadOnlyList<string> requiredCapabilities,
        RunnerKind? preferred = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RunnerRegistry : IRunnerRegistry
{
    /// <summary>What the section 27.3 message tells the reader to do about it.</summary>
    public const string RegisterHint = "Register one in Settings → Runners.";

    private readonly IReadOnlyList<IAgentRunner> _runners;

    public RunnerRegistry(IEnumerable<IAgentRunner> runners)
    {
        ArgumentNullException.ThrowIfNull(runners);
        _runners = [.. runners];
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentRunner> Runners => _runners;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> AdvertisedCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var union = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (_, descriptor) in await DescribeAllAsync(cancellationToken))
        {
            if (!descriptor.Online)
            {
                continue;
            }

            foreach (var capability in descriptor.Capabilities)
            {
                union.Add(capability);
            }
        }

        return [.. union];
    }

    /// <inheritdoc />
    public async ValueTask<RunnerRouting> RouteAsync(
        IReadOnlyList<string> requiredCapabilities,
        RunnerKind? preferred = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        var described = await DescribeAllAsync(cancellationToken);
        var eligible = new List<(IAgentRunner Runner, RunnerDescriptor Descriptor)>();

        foreach (var (runner, descriptor) in described)
        {
            if (descriptor.Online && descriptor.CanRun(requiredCapabilities))
            {
                eligible.Add((runner, descriptor));
            }
        }

        if (eligible.Count > 0)
        {
            // A session records the backend it was queued against, so honour it when that backend can
            // still run the work. Failing that, first enabled wins, which is CHARTER_RUNNER order.
            var chosen = eligible.FirstOrDefault(candidate => candidate.Runner.Kind == preferred);
            return new RunnerRouting((chosen.Runner ?? eligible[0].Runner), null, []);
        }

        return new RunnerRouting(null, Explain(described, requiredCapabilities), MissingAcrossAll(described, requiredCapabilities));
    }

    /// <summary>
    /// The section 27.3 message. Deliberately names the capability in human words and says what to do
    /// next — a dead end that does not say who to ask is how people stop using the tool (section 34.5).
    /// </summary>
    internal static string Explain(
        IReadOnlyList<(IAgentRunner Runner, RunnerDescriptor Descriptor)> described,
        IReadOnlyList<string> required)
    {
        if (described.Count == 0)
        {
            return "No runner backend is enabled on this instance. Set CHARTER_RUNNER to "
                + "agent, github-actions or docker, or register a runner in Settings → Runners.";
        }

        var missing = MissingAcrossAll(described, required);

        if (missing.Count == 0)
        {
            // Everything is advertised, so the only way to get here is that every runner that has it
            // is offline. Say that, rather than claiming a capability nobody has.
            var offline = described.Where(entry => !entry.Descriptor.Online).Select(entry => entry.Descriptor.Name);
            return $"Every runner that can build this is offline ({string.Join(", ", offline)}). "
                + "Start it, or register another one in Settings → Runners.";
        }

        return $"No runner available with {RunnerCapability.DescribeAll(missing)}. {RegisterHint}";
    }

    private static IReadOnlyList<string> MissingAcrossAll(
        IReadOnlyList<(IAgentRunner Runner, RunnerDescriptor Descriptor)> described,
        IReadOnlyList<string> required)
    {
        var advertised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, descriptor) in described)
        {
            foreach (var capability in descriptor.Capabilities)
            {
                advertised.Add(capability);
            }
        }

        return RunnerCapability.Missing(advertised, required);
    }

    private async ValueTask<IReadOnlyList<(IAgentRunner Runner, RunnerDescriptor Descriptor)>> DescribeAllAsync(
        CancellationToken cancellationToken)
    {
        var described = new List<(IAgentRunner, RunnerDescriptor)>(_runners.Count);

        foreach (var runner in _runners)
        {
            described.Add((runner, await runner.DescribeAsync(cancellationToken)));
        }

        return described;
    }
}
