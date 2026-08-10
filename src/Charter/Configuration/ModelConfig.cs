namespace Charter.Configuration;

/// <summary>
/// A provider-qualified model identifier such as <c>anthropic/claude-opus-5</c> or
/// <c>openrouter/deepseek/deepseek-r1</c> (section 20b.1).
/// </summary>
/// <remarks>
/// An unqualified identifier is treated as <c>anthropic/</c>, which is why the section 4.2 defaults
/// are written bare. A bare name that is not an Anthropic model fails startup validation rather than
/// being guessed at, because silently sending <c>gpt-4o</c> to the Anthropic API produces a runtime
/// 404 during someone's first refinement instead of a message at boot.
/// </remarks>
/// <param name="Provider">Lower-case provider segment, for example <c>anthropic</c>.</param>
/// <param name="Model">Everything after the provider, which may itself contain slashes.</param>
public sealed record ModelIdentifier(string Provider, string Model)
{
    /// <summary>The provider assumed for a bare identifier.</summary>
    public const string DefaultProvider = "anthropic";

    /// <summary>
    /// Providers Charter can route to, matching the <c>IModelClient</c> implementations and
    /// <c>CredentialGrant</c> kinds in sections 20b.1 and 20b.2.
    /// </summary>
    public static IReadOnlyList<string> KnownProviders { get; } =
    [
        "anthropic", "openai", "openrouter", "google", "gemini",
        "xai", "deepseek", "groq", "azure", "ollama", "custom",
    ];

    /// <summary>The canonical, always-qualified form.</summary>
    public string Qualified => $"{Provider}/{Model}";

    /// <summary>True when this identifier resolves to Anthropic's own API.</summary>
    public bool IsAnthropic => string.Equals(Provider, DefaultProvider, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Qualified;

    /// <summary>Parses an identifier, explaining the fault rather than guessing.</summary>
    public static bool TryParse(string raw, out ModelIdentifier? identifier, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(raw);
        identifier = null;
        problem = null;

        var value = raw.Trim();
        if (value.Length == 0)
        {
            problem = "the value is empty";
            return false;
        }

        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator < 0)
        {
            // Section 20b.1: bare names mean Anthropic. Accept only names that plausibly are one.
            if (!value.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            {
                problem =
                    $"'{value}' has no provider, and an unqualified identifier means anthropic/ " +
                    $"(section 20b.1). Write it as provider/model, for example openrouter/{value}";
                return false;
            }

            identifier = new ModelIdentifier(DefaultProvider, value);
            return true;
        }

        var providerToken = value[..separator].Trim();
        var model = value[(separator + 1)..].Trim();

        if (providerToken.Length == 0 || model.Length == 0)
        {
            problem = $"'{value}' is not in provider/model form";
            return false;
        }

        // Match case-insensitively but keep the canonical spelling from KnownProviders.
        var provider = KnownProviders.FirstOrDefault(
            known => string.Equals(known, providerToken, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            problem =
                $"'{providerToken}' is not a provider Charter can route to. Known providers: " +
                string.Join(", ", KnownProviders);
            return false;
        }

        identifier = new ModelIdentifier(provider, model);
        return true;
    }
}

/// <summary>
/// Model selection and the instance-level fallback credentials (sections 4.2, 20b).
/// </summary>
public sealed record ModelConfig
{
    /// <summary><c>CHARTER_MODEL_REFINE</c>, default <c>claude-sonnet-5</c>.</summary>
    public required ModelIdentifier Refine { get; init; }

    /// <summary><c>CHARTER_MODEL_BUILD</c>, default <c>claude-opus-5</c>. Passed to the agent CLI.</summary>
    public required ModelIdentifier Build { get; init; }

    /// <summary><c>CHARTER_MODEL_TEACH</c>, default <c>claude-sonnet-5</c>.</summary>
    public required ModelIdentifier Teach { get; init; }

    /// <summary><c>ANTHROPIC_API_KEY</c>. Instance-level fallback credential.</summary>
    public Secret? AnthropicApiKey { get; init; }

    /// <summary><c>OPENROUTER_API_KEY</c>. Instance-level fallback credential.</summary>
    public Secret? OpenRouterApiKey { get; init; }

    /// <summary>
    /// <c>CHARTER_ALLOW_SHARED_POOL</c>. Off by default: pooling a personal subscription routes other
    /// people's requests through it, which consumer plan terms may prohibit (section 20b.7).
    /// </summary>
    public required bool AllowSharedPool { get; init; }

    /// <summary>True when an instance-level key is configured, before consulting the database.</summary>
    public bool HasInstanceCredential => AnthropicApiKey is not null || OpenRouterApiKey is not null;

    internal static ModelConfig Parse(EnvReader reader)
    {
        var config = new ModelConfig
        {
            Refine = ParseModel(reader, "CHARTER_MODEL_REFINE", "claude-sonnet-5"),
            Build = ParseModel(reader, "CHARTER_MODEL_BUILD", "claude-opus-5"),
            Teach = ParseModel(reader, "CHARTER_MODEL_TEACH", "claude-sonnet-5"),
            AnthropicApiKey = reader.OptionalSecret("ANTHROPIC_API_KEY"),
            OpenRouterApiKey = reader.OptionalSecret("OPENROUTER_API_KEY"),
            AllowSharedPool = reader.Bool("CHARTER_ALLOW_SHARED_POOL", false),
        };

        if (!config.HasInstanceCredential)
        {
            // Section 4.2, footnote *: at least one model credential must be resolvable at startup -
            // either an instance-level key or a linked CredentialGrant in the database. The parser
            // cannot see the database, so it says so plainly and the section 30.1 preflight check
            // settles it. Claiming the instance is unusable here would be wrong for the operator who
            // linked a credential through the UI.
            reader.Warn(
                "ANTHROPIC_API_KEY",
                "no instance-level model credential is set. Set ANTHROPIC_API_KEY or OPENROUTER_API_KEY, " +
                "or link a credential in the database (section 20b.2) - the model-credential preflight " +
                "check confirms which, and startup fails if neither exists.");
        }

        return config;
    }

    private static ModelIdentifier ParseModel(EnvReader reader, string variable, string fallback)
    {
        var raw = reader.Optional(variable) ?? fallback;
        if (ModelIdentifier.TryParse(raw, out var identifier, out var problem) && identifier is not null)
        {
            return identifier;
        }

        reader.Error(variable, $"{variable} is not a usable model identifier: {problem}");
        return new ModelIdentifier(ModelIdentifier.DefaultProvider, fallback);
    }
}
