namespace Charter.Adapters;

/// <summary>
/// The credential kinds an adapter's <c>auth</c> block may name, and the provider each one buys
/// access to.
/// </summary>
/// <remarks>
/// <para>
/// The kinds come from <c>CredentialGrant.kind</c> in section 20b.2, with one addition:
/// <c>cursor_api_key</c>. Cursor authenticates against a Cursor account rather than against the
/// underlying model provider, so no section 20b.2 kind describes it. It is listed here so
/// <c>adapters/cursor-agent.yml</c> loads without a warning; until <c>CredentialGrant</c> grows the
/// kind, no credential of that kind can exist and the compatibility resolver offers nothing for that
/// adapter — which is the visible, honest failure rather than a dispatch-time one.
/// </para>
/// <para>
/// An <c>auth</c> key that is not listed here is a warning, never an error (section 8): an adapter
/// written for a newer Charter that knows about a provider this one does not must still load.
/// </para>
/// </remarks>
public static class AdapterCredentialKinds
{
    /// <summary>Provider identifier for OpenAI-compatible endpoints that are not a named provider.</summary>
    public const string CustomProvider = "custom";

    private static readonly Dictionary<string, string> ProviderByKind = new(StringComparer.Ordinal)
    {
        ["anthropic_oauth"] = "anthropic",
        ["anthropic_api_key"] = "anthropic",
        ["openai_oauth"] = "openai",
        ["openai_api_key"] = "openai",
        ["google_api_key"] = "google",
        ["xai_api_key"] = "xai",
        ["openrouter_key"] = "openrouter",
        ["custom_openai_compatible"] = CustomProvider,
        ["cursor_api_key"] = "cursor",
    };

    /// <summary>Every credential kind Charter recognises in an adapter file, sorted.</summary>
    public static IReadOnlyList<string> All { get; } = [.. ProviderByKind.Keys.Order(StringComparer.Ordinal)];

    public static bool IsKnown(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return ProviderByKind.ContainsKey(kind);
    }

    /// <summary>The provider a credential kind grants access to, or <see langword="null"/> if unknown.</summary>
    public static string? ProviderFor(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return ProviderByKind.GetValueOrDefault(kind);
    }
}
