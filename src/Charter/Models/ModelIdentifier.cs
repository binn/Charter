using System.Diagnostics.CodeAnalysis;

namespace Charter.Models;

/// <summary>
/// The wire protocol a provider speaks. Section 20b.1: three client implementations cover every
/// required provider, so several providers share one transport.
/// </summary>
public enum ModelProvider
{
    /// <summary>Anthropic's Messages API. The default for unqualified identifiers.</summary>
    Anthropic,

    /// <summary>OpenAI's own <c>/chat/completions</c> endpoint.</summary>
    OpenAi,

    /// <summary>OpenRouter. Model names are themselves slash-qualified, e.g. <c>deepseek/deepseek-r1</c>.</summary>
    OpenRouter,

    /// <summary>xAI / Grok, over the OpenAI-compatible shim.</summary>
    XAi,

    /// <summary>DeepSeek, over the OpenAI-compatible shim.</summary>
    DeepSeek,

    /// <summary>Groq, over the OpenAI-compatible shim.</summary>
    Groq,

    /// <summary>Azure OpenAI. Requires a per-credential base URL naming the deployment.</summary>
    AzureOpenAi,

    /// <summary>Ollama, over the OpenAI-compatible shim. Typically self-hosted.</summary>
    Ollama,

    /// <summary>Google Gemini's native API.</summary>
    Google,

    /// <summary>Anything else exposing <c>/chat/completions</c> at a configured base URL.</summary>
    OpenAiCompatible,
}

/// <summary>
/// A provider-qualified model identifier: <c>anthropic/claude-opus-5</c>,
/// <c>openrouter/deepseek/deepseek-r1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 20b.1. The provider prefix is the text before the <em>first</em> slash and the model is
/// everything after it - OpenRouter model names contain their own slash, so splitting on every
/// separator loses the vendor half of the name.
/// </para>
/// <para>
/// An unqualified identifier resolves to <see cref="ModelProvider.Anthropic"/>, which is why the
/// section 4.2 defaults are written bare. A bare name is never guessed at against another provider.
/// </para>
/// </remarks>
public sealed record ModelIdentifier
{
    private ModelIdentifier(ModelProvider provider, string model, bool wasQualified)
    {
        Provider = provider;
        Model = model;
        WasQualified = wasQualified;
    }

    /// <summary>The resolved provider.</summary>
    public ModelProvider Provider { get; }

    /// <summary>
    /// The provider-local model name, with the provider prefix removed. For OpenRouter this still
    /// contains a slash, e.g. <c>deepseek/deepseek-r1</c>.
    /// </summary>
    public string Model { get; }

    /// <summary>
    /// <see langword="true"/> when the source string carried an explicit provider prefix. Startup
    /// validation uses this to tell "the operator meant Anthropic" from "the operator wrote a bare
    /// name that no Anthropic model matches".
    /// </summary>
    public bool WasQualified { get; }

    /// <summary>The canonical, always-qualified form.</summary>
    public string Canonical => $"{ProviderPrefix(Provider)}/{Model}";

    /// <inheritdoc />
    public override string ToString() => Canonical;

    /// <summary>Creates an identifier from an already-resolved provider and model name.</summary>
    /// <exception cref="ArgumentException">The model name is empty.</exception>
    public static ModelIdentifier Create(ModelProvider provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new ModelIdentifier(provider, model.Trim(), wasQualified: true);
    }

    /// <summary>Parses a provider-qualified or bare model identifier.</summary>
    /// <exception cref="FormatException">The value is empty or names an unknown provider.</exception>
    public static ModelIdentifier Parse(string value)
    {
        if (!TryParse(value, out var identifier, out var error))
        {
            throw new FormatException(error);
        }

        return identifier;
    }

    /// <summary>Attempts to parse a provider-qualified or bare model identifier.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ModelIdentifier? identifier) =>
        TryParse(value, out identifier, out _);

    /// <summary>
    /// Attempts to parse a provider-qualified or bare model identifier, reporting why it failed.
    /// </summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out ModelIdentifier? identifier,
        [NotNullWhen(false)] out string? error)
    {
        identifier = null;
        error = null;

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "A model identifier cannot be empty.";
            return false;
        }

        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (separator < 0)
        {
            // Unqualified: Anthropic, per section 20b.1.
            identifier = new ModelIdentifier(ModelProvider.Anthropic, trimmed, wasQualified: false);
            return true;
        }

        if (separator == 0 || separator == trimmed.Length - 1)
        {
            error = $"'{trimmed}' is not a valid model identifier. Expected 'provider/model'.";
            return false;
        }

        var prefix = trimmed[..separator];
        // Everything after the first slash is the model name. OpenRouter nests a vendor segment
        // inside it (openrouter/deepseek/deepseek-r1) and that segment must survive parsing.
        var model = trimmed[(separator + 1)..];

        if (!TryParseProvider(prefix, out var provider))
        {
            error =
                $"'{prefix}' is not a known model provider. Known providers: " +
                $"{string.Join(", ", KnownProviderPrefixes)}.";
            return false;
        }

        identifier = new ModelIdentifier(provider, model, wasQualified: true);
        return true;
    }

    /// <summary>The prefixes accepted by <see cref="TryParse(string?, out ModelIdentifier?)"/>.</summary>
    public static IReadOnlyList<string> KnownProviderPrefixes { get; } =
    [
        "anthropic",
        "openai",
        "openrouter",
        "xai",
        "deepseek",
        "groq",
        "azure",
        "ollama",
        "google",
        "openai-compatible",
    ];

    /// <summary>The canonical prefix for a provider.</summary>
    public static string ProviderPrefix(ModelProvider provider) => provider switch
    {
        ModelProvider.Anthropic => "anthropic",
        ModelProvider.OpenAi => "openai",
        ModelProvider.OpenRouter => "openrouter",
        ModelProvider.XAi => "xai",
        ModelProvider.DeepSeek => "deepseek",
        ModelProvider.Groq => "groq",
        ModelProvider.AzureOpenAi => "azure",
        ModelProvider.Ollama => "ollama",
        ModelProvider.Google => "google",
        ModelProvider.OpenAiCompatible => "openai-compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private static bool TryParseProvider(string prefix, out ModelProvider provider)
    {
        switch (prefix.ToLowerInvariant())
        {
            case "anthropic":
            case "claude":
                provider = ModelProvider.Anthropic;
                return true;
            case "openai":
                provider = ModelProvider.OpenAi;
                return true;
            case "openrouter":
                provider = ModelProvider.OpenRouter;
                return true;
            case "xai":
            case "grok":
                provider = ModelProvider.XAi;
                return true;
            case "deepseek":
                provider = ModelProvider.DeepSeek;
                return true;
            case "groq":
                provider = ModelProvider.Groq;
                return true;
            case "azure":
            case "azure-openai":
                provider = ModelProvider.AzureOpenAi;
                return true;
            case "ollama":
                provider = ModelProvider.Ollama;
                return true;
            case "google":
            case "gemini":
                provider = ModelProvider.Google;
                return true;
            case "openai-compatible":
            case "custom":
                provider = ModelProvider.OpenAiCompatible;
                return true;
            default:
                provider = default;
                return false;
        }
    }
}
