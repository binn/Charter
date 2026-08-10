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

/// <summary>
/// The form of model identifier the agent CLI expects on its command line (section 12b,
/// <c>model_format</c>).
/// </summary>
/// <remarks>
/// The CLIs disagree, and the disagreement is not cosmetic: <c>claude --model anthropic/claude-opus-5</c>
/// is not a model Claude Code knows, and <c>opencode run --model claude-opus-5</c> is not a model
/// OpenCode knows. Charter therefore holds one canonical identifier (section 20b.1) and each adapter
/// declares how to render it, rather than every caller guessing per agent.
/// </remarks>
public enum AdapterModelFormat
{
    /// <summary>
    /// The provider-local model name, with Charter's provider prefix removed: <c>claude-opus-5</c>,
    /// and <c>deepseek/deepseek-r1</c> for <c>openrouter/deepseek/deepseek-r1</c> - an OpenRouter model
    /// id keeps its own vendor segment. The default, and what every CLI that talks to one provider at a
    /// time wants.
    /// </summary>
    Bare,

    /// <summary>
    /// Charter's full provider-qualified identifier, <c>anthropic/claude-opus-5</c>. OpenCode's
    /// <c>--model</c> takes exactly this shape.
    /// </summary>
    Qualified,

    /// <summary>
    /// Substituted exactly as the caller supplied it, with no reinterpretation. For a CLI whose model
    /// names follow a third-party scheme Charter cannot derive - aider resolves through LiteLLM, whose
    /// names are neither Charter's bare form nor Charter's qualified form for every provider.
    /// </summary>
    Verbatim,
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
    string SourcePath,
    AdapterModelFormat ModelFormat = AdapterModelFormat.Bare)
{
    /// <summary>The placeholder substituted with the resolved model identifier in <c>model_arg</c>.</summary>
    public const string ModelPlaceholder = "{model}";

    /// <summary>
    /// The placeholder substituted with the model's provider segment in <c>model_arg</c>, for a CLI
    /// that selects the provider with its own flag. Pi's <c>--provider openrouter --model
    /// deepseek/deepseek-r1</c> is the case this exists for.
    /// </summary>
    public const string ProviderPlaceholder = "{provider}";

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
    /// Renders Charter's canonical model identifier in the form this CLI expects.
    /// </summary>
    /// <remarks>
    /// Idempotent for <see cref="AdapterModelFormat.Bare"/>: a name that already carries no provider
    /// is Anthropic's (section 20b.1), and stripping a prefix that is not there leaves it alone. That
    /// is what lets a caller pass either form without the dispatched command changing.
    /// </remarks>
    public string FormatModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (ModelFormat == AdapterModelFormat.Verbatim)
        {
            return model.Trim();
        }

        var identifier = ModelIdentifier.Parse(model);
        return ModelFormat == AdapterModelFormat.Qualified ? identifier.ToString() : identifier.Name;
    }

    /// <summary>
    /// Builds the argument vector and standard input for one run. No shell is involved, so nothing
    /// here is quoted or escaped and <c>&amp;&amp;</c> in a command would be a literal argument.
    /// </summary>
    /// <param name="prompt">The refined spec.</param>
    /// <param name="model">
    /// Charter's canonical model identifier, qualified or bare (section 20b.1). It is rendered into
    /// <c>model_arg</c> in the form this adapter declared through <see cref="ModelFormat"/>, so the
    /// caller passes one identifier and never has to know which agent it is dispatching to.
    /// </param>
    public AdapterInvocation BuildInvocation(string prompt, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var arguments = new List<string>(Invoke.Command);

        if (model is not null)
        {
            var rendered = FormatModel(model);
            var provider = ModelIdentifier.Parse(model).Provider;

            foreach (var argument in ModelArg)
            {
                arguments.Add(argument
                    .Replace(ModelPlaceholder, rendered, StringComparison.Ordinal)
                    .Replace(ProviderPlaceholder, provider, StringComparison.Ordinal));
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
