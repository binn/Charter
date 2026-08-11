namespace Charter.Hosting;

/// <summary>
/// The registration and routing extension methods the host deliberately does <em>not</em> call, and
/// why.
/// </summary>
/// <remarks>
/// <para>
/// <c>HostCompositionTests</c> reflects over the assembly for every <c>AddCharter*</c> and
/// <c>MapCharter*</c> extension method and requires each one to be reachable from
/// <see cref="CharterHost.ConfigureServices"/> or <see cref="CharterHost.ConfigurePipeline"/> — or to
/// appear here with a reason. That makes the <em>absence</em> of a call the thing that fails a test,
/// which is the only way the class of defect this exists for gets caught: an entire subsystem was
/// once unregistered in the running app and nothing noticed, because an endpoint happened to resolve
/// a type another module had registered.
/// </para>
/// <para>
/// Adding a name here is a decision, not a way to quiet a test. The reason string is what a reviewer
/// reads, so it must say why the host is right not to compose it — not merely that it does not.
/// </para>
/// </remarks>
public static class CharterComposition
{
    /// <summary>Extension method name to the reason the host does not call it.</summary>
    /// <remarks>
    /// Worth keeping empty. Every registration Charter ships should be composed by the running
    /// application; the dictionary exists so that the day one of them is not, the omission is written
    /// down where a reviewer sees it rather than expressed as a call that quietly is not there. An
    /// entry whose reason begins <c>TODO: wire</c> is a queue, not a decision — it means the subsystem
    /// is built and the host call has not landed yet.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> DeliberatelyNotComposed { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Extension methods a caller reaches only through another one the host does call.</summary>
    /// <remarks>
    /// These are not exemptions: the composition test walks the call graph, so
    /// <c>AddCharterVersionControl</c> counts as composed because <c>AddCharterGitHub</c> calls it.
    /// The set exists only so the failure message can say which route was expected.
    /// </remarks>
    public static IReadOnlySet<string> ComposedIndirectly { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AddCharterVersionControl",
            "AddCharterAgentPlane",
            "AddCharterNotifications",
            "AddCharterStores",
            "MapCharterApiEndpoints",
        };
}
