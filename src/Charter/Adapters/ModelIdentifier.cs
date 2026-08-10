namespace Charter.Adapters;

/// <summary>
/// A provider-qualified model identifier, for example <c>anthropic/claude-opus-5</c> or
/// <c>openrouter/deepseek/deepseek-r1</c> (section 20b.1).
/// </summary>
/// <remarks>
/// An unqualified identifier is Anthropic's, which is why the section 4.2 defaults are written bare.
/// Only the first segment is the provider: everything after it is the provider's own model name and
/// may itself contain slashes.
/// </remarks>
public readonly record struct ModelIdentifier(string Provider, string Name)
{
    /// <summary>The provider an unqualified identifier belongs to (section 20b.1).</summary>
    public const string DefaultProvider = "anthropic";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = "google",
        ["google-gemini"] = "google",
        ["grok"] = "xai",
        ["azure"] = "openai",
        ["azure-openai"] = "openai",

        // `openai-compatible` is the canonical prefix the model layer prints for a self-hosted or
        // proxied endpoint; `custom` is what CredentialGrant calls the matching credential kind
        // (custom_openai_compatible). Folding them together is what stops an adapter that declares
        // that kind from being refused a model it can in fact run.
        ["openai-compatible"] = AdapterCredentialKinds.CustomProvider,
        ["custom"] = AdapterCredentialKinds.CustomProvider,
    };

    public static ModelIdentifier Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == trimmed.Length - 1)
        {
            return new ModelIdentifier(DefaultProvider, trimmed);
        }

        var provider = trimmed[..slash];
        return new ModelIdentifier(Canonicalise(provider), trimmed[(slash + 1)..]);
    }

    /// <summary>Folds provider spellings that mean the same thing onto one name.</summary>
    public static string Canonicalise(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Aliases.TryGetValue(provider, out var canonical) ? canonical : provider.ToLowerInvariant();
    }

    public override string ToString() => $"{Provider}/{Name}";
}
