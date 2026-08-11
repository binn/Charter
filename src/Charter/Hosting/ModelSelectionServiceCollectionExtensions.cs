using Charter.Configuration;
using Charter.Models;
using Charter.Recaps;
using Charter.Refinement;
using Charter.Teaching;
using Microsoft.Extensions.DependencyInjection;
using ConfigModelIdentifier = Charter.Configuration.ModelIdentifier;
using ModelIdentifier = Charter.Models.ModelIdentifier;

namespace Charter.Hosting;

/// <summary>
/// Projects the section 4.2 model selection onto the subsystems that make control-plane calls.
/// </summary>
/// <remarks>
/// <para>
/// <c>CHARTER_MODEL_REFINE</c> and <c>CHARTER_MODEL_TEACH</c> were parsed, validated, and then
/// ignored. Refinement, teaching and the engineer recap each register their own options record with
/// <c>TryAddSingleton</c> and a hardcoded <c>claude-sonnet-4-6</c> on Anthropic, and every one of
/// those <c>TryAdd</c> comments says "a host projecting <c>CharterConfig</c> into it can register
/// first and win" - which no host did. The consequence was not cosmetic: the documented default
/// install is OpenRouter-only, so every refinement on a default instance presented an OpenRouter key
/// to <c>api.anthropic.com</c>, took a 401, and had the credential marked <c>invalid</c> (section
/// 20b.4) for a fault that was entirely Charter's.
/// </para>
/// <para>
/// <strong>The build model is deliberately not here.</strong> Section 20b opens by distinguishing two
/// surfaces, and this method covers exactly one of them. Refinement, teaching and recap are calls
/// Charter's own HTTP client makes, so they may name any provider Charter holds a key for.
/// <c>CHARTER_MODEL_BUILD</c> is a string handed to an agent CLI on a runner, bounded by what that
/// CLI can authenticate against; it is already wired, through <c>AddCharterRunners</c>, into
/// <c>OrchestrationOptions.BuildModel</c>, and its bare Anthropic default is correct rather than an
/// oversight to be tidied into matching the other two.
/// </para>
/// </remarks>
public static class ModelSelectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ModelClientOptions"/>, <see cref="RefinementOptions"/>,
    /// <see cref="TeachingOptions"/> and <see cref="RecapOptions"/> from <paramref name="config"/>.
    /// </summary>
    /// <remarks>
    /// Must be called <em>before</em> <c>AddCharterModels</c>, <c>AddCharterRefinement</c>,
    /// <c>AddCharterRecap</c> and <c>AddCharterTeaching</c>: all four register their defaults with
    /// <c>TryAdd</c>, so the first registration wins and it has to be this one.
    /// </remarks>
    public static IServiceCollection AddCharterModelSelection(
        this IServiceCollection services,
        CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var refine = Translate(config.Models.Refine);
        var teach = Translate(config.Models.Teach);

        services.AddSingleton(new ModelClientOptions
        {
            RefineModel = refine,
            TeachModel = teach,
            BuildModel = Translate(config.Models.Build),

            // Section 20b.6: OpenRouter attributes usage to the referring application. Charter's own
            // public URL is the honest answer, and it is never a requester's.
            OpenRouterReferer = config.BaseUrl,
        });

        services.AddSingleton(new RefinementOptions { Model = refine });

        // Section 14: the recap is a control-plane call over the same transcript as teaching, and
        // section 4.2 gives it no variable of its own. Teaching's model is the closer match of the
        // two - both summarise a finished session for a human - so a recap follows CHARTER_MODEL_TEACH
        // rather than silently staying on a hardcoded name no variable can move.
        services.AddSingleton(new RecapOptions { Model = teach });

        services.AddSingleton(new TeachingOptions { Model = teach });

        // Section 20b.7: the operator's terms-of-service decision, projected onto the one place that
        // can act on it. CredentialResolver used to decide pool membership purely per grant, which
        // made CHARTER_ALLOW_SHARED_POOL inert in both positions.
        services.AddSingleton(new CredentialPolicy(config.Models.AllowSharedPool));

        return services;
    }

    /// <summary>
    /// Converts a parsed section 4.2 identifier into the model layer's own.
    /// </summary>
    /// <remarks>
    /// Two <c>ModelIdentifier</c> types exist on purpose: configuration parses text and reports
    /// problems without knowing which client speaks what, and the model layer resolves a transport
    /// without knowing that environment variables exist. Every provider the configuration parser
    /// accepts is one the model layer's parser also accepts - including its aliases,
    /// <c>gemini</c> for Google and <c>custom</c> for an OpenAI-compatible endpoint - so the
    /// canonical qualified form round-trips, and the parse cannot fail on a value startup validation
    /// has already accepted.
    /// </remarks>
    private static ModelIdentifier Translate(ConfigModelIdentifier identifier)
        => ModelIdentifier.Parse(identifier.Qualified);
}
