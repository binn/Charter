using System.Diagnostics.CodeAnalysis;

namespace Charter.Adapters;

/// <summary>How an adapter's output stream can be read (section 12b, <c>events.format</c>).</summary>
public enum AdapterEventFormat
{
    /// <summary>One JSON object per line. The only format that gives the requester a real pane 2.</summary>
    Jsonl,

    /// <summary>Human-formatted text. Degrades to a raw log; see docs/adapters.md.</summary>
    Text,
}

/// <summary>The Charter event types an adapter line can be classified into (section 12b).</summary>
public enum AdapterEventType
{
    /// <summary>Agent prose. The raw material for milestone promotion into the status thread.</summary>
    Message,

    /// <summary>Any tool invocation.</summary>
    ToolUse,

    /// <summary>A tool invocation that writes a file. What pane 3 links from.</summary>
    FileWrite,
}

/// <summary>What an adapter supports (section 12b, <c>capabilities</c>).</summary>
public enum AdapterCapability
{
    /// <summary>Further instructions can be delivered mid-run.</summary>
    Steering,

    /// <summary>A session can be continued after an interruption.</summary>
    Resume,

    /// <summary>The CLI reports what a run cost, so Charter does not have to estimate.</summary>
    CostReporting,
}

/// <summary>How the refined spec reaches the agent (section 12b, <c>invoke.prompt</c>).</summary>
public enum AdapterPromptDelivery
{
    /// <summary>Written to the process's standard input.</summary>
    Stdin,

    /// <summary>Appended as one more argument, built from a template containing <c>{prompt}</c>.</summary>
    Argument,
}

/// <summary>How Charter detects that the agent CLI is present on the runner.</summary>
public sealed record AdapterInstall(string Check, string Hint);

/// <summary>How the agent CLI is started.</summary>
public sealed record AdapterInvoke(
    IReadOnlyList<string> Command,
    AdapterPromptDelivery PromptDelivery,
    string? PromptArgumentTemplate);

/// <summary>One entry of the <c>auth</c> block: a Charter credential kind and the variable the CLI reads.</summary>
public sealed record AdapterAuthBinding(string CredentialKind, string EnvironmentVariable);

/// <summary>One entry of <c>events.map</c>, keeping the source text next to the parsed predicate.</summary>
public sealed record AdapterEventMapping(AdapterEventType EventType, string Source, EventExpression Predicate);

/// <summary>The <c>events</c> block.</summary>
public sealed record AdapterEvents(AdapterEventFormat Format, IReadOnlyList<AdapterEventMapping> Map);

/// <summary>An argument vector plus the standard input to feed it, ready to hand to a process.</summary>
public sealed record AdapterInvocation(IReadOnlyList<string> Arguments, string? StandardInput);

/// <summary>
/// One adapter YAML file, parsed and validated (section 12b).
/// </summary>
/// <remarks>
/// Adapters are data. Nothing in Charter branches on <see cref="Id"/>: everything an agent-specific
/// code path would have done is expressed by the fields below, which is what makes supporting a new
/// coding agent a configuration pull request rather than a release.
/// </remarks>
public sealed record AdapterDocument(
    string Id,
    string DisplayName,
    int Version,
    AdapterInstall Install,
    AdapterInvoke Invoke,
    IReadOnlyList<AdapterAuthBinding> Auth,
    IReadOnlyList<string> ModelArg,
    AdapterEvents Events,
    IReadOnlyList<AdapterCapability> Capabilities,
    string SourcePath)
{
    /// <summary>The placeholder substituted with the resolved model identifier in <c>model_arg</c>.</summary>
    public const string ModelPlaceholder = "{model}";

    /// <summary>The placeholder substituted with the refined spec in an argument-delivered prompt.</summary>
    public const string PromptPlaceholder = "{prompt}";

    /// <summary>True when this adapter emits a stream Charter can classify into events.</summary>
    public bool IsStructured => Events.Format == AdapterEventFormat.Jsonl;

    public bool Supports(AdapterCapability capability) => Capabilities.Contains(capability);

    /// <summary>The Charter credential kinds this adapter can consume, in declaration order.</summary>
    public IEnumerable<string> CredentialKinds => Auth.Select(binding => binding.CredentialKind);

    /// <summary>The environment variable a given credential kind must be injected as, if any.</summary>
    public bool TryGetEnvironmentVariable(string credentialKind, [NotNullWhen(true)] out string? name)
    {
        ArgumentNullException.ThrowIfNull(credentialKind);

        foreach (var binding in Auth)
        {
            if (string.Equals(binding.CredentialKind, credentialKind, StringComparison.Ordinal))
            {
                name = binding.EnvironmentVariable;
                return true;
            }
        }

        name = null;
        return false;
    }

    /// <summary>
    /// Builds the argument vector and standard input for one run. No shell is involved, so nothing
    /// here is quoted or escaped and <c>&amp;&amp;</c> in a command would be a literal argument.
    /// </summary>
    /// <param name="prompt">The refined spec.</param>
    /// <param name="model">
    /// Substituted into <c>model_arg</c> verbatim. The caller decides the form, because the CLIs
    /// disagree: <c>opencode</c> wants the provider-qualified <c>anthropic/claude-opus-5</c> while
    /// <c>claude</c> wants the bare <c>claude-opus-5</c>. Use <see cref="ModelIdentifier.Parse"/> and
    /// pass <c>Name</c> for the latter.
    /// </param>
    public AdapterInvocation BuildInvocation(string prompt, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var arguments = new List<string>(Invoke.Command);

        if (model is not null)
        {
            foreach (var argument in ModelArg)
            {
                arguments.Add(argument.Replace(ModelPlaceholder, model, StringComparison.Ordinal));
            }
        }

        if (Invoke.PromptDelivery == AdapterPromptDelivery.Stdin)
        {
            return new AdapterInvocation(arguments, prompt);
        }

        arguments.Add(Invoke.PromptArgumentTemplate!.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal));
        return new AdapterInvocation(arguments, null);
    }
}
