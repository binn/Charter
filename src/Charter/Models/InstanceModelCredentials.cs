using System.Collections.Concurrent;

namespace Charter.Models;

/// <summary>
/// One instance-level key, as configured by an environment variable rather than stored as a row.
/// </summary>
/// <param name="Variable">The environment variable it came from, which is what an operator edits.</param>
/// <param name="Kind">The credential kind it authenticates as, which decides its tier.</param>
/// <param name="Secret">The key. Never logged, never returned by an API.</param>
public sealed record InstanceModelKey(string Variable, ModelCredentialKind Kind, ModelSecret Secret);

/// <summary>
/// What an instance-level key looks like from outside: everything except the key.
/// </summary>
/// <param name="Variable">The environment variable it came from.</param>
/// <param name="Kind">The credential kind.</param>
/// <param name="Status">Its live state, which a <c>429</c> or a <c>401</c> moves.</param>
/// <param name="ExhaustedUntil">When capacity returns, if a provider said.</param>
/// <param name="InvalidReason">Why it was rejected, in short prose. Never credential material.</param>
/// <param name="LastUsedAt">The last successful call made with it.</param>
public sealed record InstanceModelKeyStatus(
    string Variable,
    ModelCredentialKind Kind,
    ModelCredentialStatus Status,
    DateTimeOffset? ExhaustedUntil,
    string? InvalidReason,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// The section 4.2 instance-level keys — <c>ANTHROPIC_API_KEY</c> and <c>OPENROUTER_API_KEY</c> — as
/// candidates the section 20b.3 chain can actually resolve.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type exists.</strong> Both variables were parsed, validated, and accepted by the
/// section 30.1 preflight check, and then never consulted again: resolution read
/// <c>credential_grants</c> and nothing else, and no API created a grant. The documented default
/// install therefore booted healthy, reported a passing model-credential check, and then sat every
/// request in <c>refining</c> forever. An instance-level key that satisfies startup has to be a tier
/// in the chain, not a fact recorded at boot.
/// </para>
/// <para>
/// <strong>Which tier.</strong> Neither key gets a tier of its own. Section 20b.3 has five, and an
/// instance key is exactly two of them already: <c>ANTHROPIC_API_KEY</c> is an organisation metered
/// API key (tier 4) and <c>OPENROUTER_API_KEY</c> is OpenRouter (tier 5). They are therefore
/// projected as ordinary <see cref="ModelCredential"/> candidates of the matching kind and land in
/// their own tier by the same rule every stored grant follows. Within the tier they sort last, via
/// <see cref="ModelCredential.Priority"/> of <see cref="int.MaxValue"/>: the variables are documented
/// as the instance's <em>fallback</em>, so a key an operator linked deliberately for this
/// organisation is the one that should serve.
/// </para>
/// <para>
/// <strong>Never persisted.</strong> The environment is the source of truth for these, so nothing is
/// written to <c>credential_grants</c> — a row would diverge the moment the variable changed, and it
/// would need a schema marker to know it was environment-derived. The consequence is that the two
/// facts resolution writes back, exhaustion and invalidity, are held here in memory. That is not the
/// orchestration state section 2.3 forbids: it is a rate-limit note whose worst-case loss on restart
/// is one retried call, and a restart is also exactly when an operator has just changed the key.
/// </para>
/// </remarks>
public sealed class InstanceModelCredentials
{
    /// <summary>The prefix every instance-level credential id carries.</summary>
    /// <remarks>
    /// A grant id is a <see cref="Guid"/>, so no stored row can collide with one of these, and the
    /// prefix is what lets the store tell a write-back meant for a row from one meant for a variable.
    /// </remarks>
    public const string IdPrefix = "env:";

    /// <summary><c>ANTHROPIC_API_KEY</c>.</summary>
    public const string AnthropicVariable = "ANTHROPIC_API_KEY";

    /// <summary><c>OPENROUTER_API_KEY</c>.</summary>
    public const string OpenRouterVariable = "OPENROUTER_API_KEY";

    private readonly IReadOnlyList<InstanceModelKey> _keys;
    private readonly ConcurrentDictionary<string, State> _state = new(StringComparer.Ordinal);

    /// <summary>Creates the instance-level credential set.</summary>
    /// <param name="keys">The configured keys, in any order. An empty list is legitimate.</param>
    public InstanceModelCredentials(IEnumerable<InstanceModelKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _keys = keys
            .Where(key => key.Secret.HasValue)
            .DistinctBy(key => key.Variable, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>An instance with no environment key at all.</summary>
    public static InstanceModelCredentials None { get; } = new([]);

    /// <summary>Builds the set from the two section 4.2 variables.</summary>
    /// <param name="anthropicApiKey"><c>ANTHROPIC_API_KEY</c>, or <see langword="null"/>.</param>
    /// <param name="openRouterApiKey"><c>OPENROUTER_API_KEY</c>, or <see langword="null"/>.</param>
    public static InstanceModelCredentials From(string? anthropicApiKey, string? openRouterApiKey)
    {
        var keys = new List<InstanceModelKey>(2);

        if (!string.IsNullOrWhiteSpace(anthropicApiKey))
        {
            keys.Add(new InstanceModelKey(
                AnthropicVariable,
                ModelCredentialKind.AnthropicApiKey,
                new ModelSecret(anthropicApiKey)));
        }

        if (!string.IsNullOrWhiteSpace(openRouterApiKey))
        {
            keys.Add(new InstanceModelKey(
                OpenRouterVariable,
                ModelCredentialKind.OpenRouterKey,
                new ModelSecret(openRouterApiKey)));
        }

        return new InstanceModelCredentials(keys);
    }

    /// <summary>Whether this instance has any environment key configured.</summary>
    public bool Any => _keys.Count > 0;

    /// <summary>The variables that are set, for a message an operator reads.</summary>
    public IReadOnlyList<string> Variables => _keys.Select(key => key.Variable).ToList();

    /// <summary>
    /// The environment variables that could serve <paramref name="provider"/>, whether or not they
    /// are set on this instance.
    /// </summary>
    /// <remarks>
    /// This is what makes a credential failure actionable rather than merely loud: the message names
    /// the variable to set for the model that could not be served, and an <c>openrouter/</c>-qualified
    /// model is served by an OpenRouter key alone however many Anthropic keys are present.
    /// </remarks>
    public static IReadOnlyList<string> VariablesFor(ModelProvider provider) => provider switch
    {
        ModelProvider.OpenRouter => [OpenRouterVariable],
        ModelProvider.Anthropic => [AnthropicVariable, OpenRouterVariable],

        // Every other provider is reachable through OpenRouter, and through no instance-level key of
        // its own: section 4.2 defines exactly two variables.
        _ => [OpenRouterVariable],
    };

    /// <summary>Whether <paramref name="credentialId"/> names one of these rather than a stored row.</summary>
    public bool Owns(string? credentialId)
        => credentialId is not null && credentialId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The instance-level candidates, projected with their current status.
    /// </summary>
    /// <remarks>
    /// Whether a candidate can serve the requested model is not decided here. The resolver applies
    /// the same eligibility rule to these as to a stored grant, so an <c>ANTHROPIC_API_KEY</c> is
    /// never offered to an <c>openrouter/</c> model by accident.
    /// </remarks>
    public IReadOnlyList<ModelCredential> Candidates()
    {
        if (_keys.Count == 0)
        {
            return [];
        }

        var candidates = new List<ModelCredential>(_keys.Count);

        foreach (var key in _keys)
        {
            var state = _state.GetValueOrDefault(IdFor(key.Variable)) ?? State.Fresh;

            candidates.Add(new ModelCredential
            {
                Id = IdFor(key.Variable),
                Kind = key.Kind,
                Secret = key.Secret,

                // No owner and no organisation: an instance-level key belongs to the deployment, so
                // it must serve every organisation on it and can never be tier 1's "the requester's
                // own". Tiers 4 and 5 do not filter on either field.
                OwnerUserId = null,
                OrganizationId = null,
                Scope = ModelCredentialScope.Personal,
                Status = state.Status,
                ExhaustedUntil = state.ExhaustedUntil,

                // Last within its tier. The variables are the instance's fallback, so a grant an
                // operator linked for this organisation should win where both could serve.
                Priority = int.MaxValue,
            });
        }

        return candidates;
    }

    /// <summary>Everything about the configured keys except the keys. Safe to return from an API.</summary>
    public IReadOnlyList<InstanceModelKeyStatus> Describe()
    {
        var described = new List<InstanceModelKeyStatus>(_keys.Count);

        foreach (var key in _keys)
        {
            var state = _state.GetValueOrDefault(IdFor(key.Variable)) ?? State.Fresh;

            described.Add(new InstanceModelKeyStatus(
                key.Variable,
                key.Kind,
                state.Status,
                state.ExhaustedUntil,
                state.InvalidReason,
                state.LastUsedAt));
        }

        return described;
    }

    /// <summary>The variable behind an instance-level credential id, for a log line or a message.</summary>
    public string? VariableFor(string credentialId)
    {
        ArgumentNullException.ThrowIfNull(credentialId);

        return _keys
            .FirstOrDefault(key => string.Equals(IdFor(key.Variable), credentialId, StringComparison.Ordinal))
            ?.Variable;
    }

    /// <summary>Section 20b.4: a <c>429</c> exhausts the key until the provider's reset instant.</summary>
    public void MarkExhausted(string credentialId, DateTimeOffset? until)
        => Update(credentialId, state => state with
        {
            Status = ModelCredentialStatus.Exhausted,
            ExhaustedUntil = until,
            InvalidReason = null,
        });

    /// <summary>A hard authentication failure. Waiting will not fix it; changing the variable will.</summary>
    public void MarkInvalid(string credentialId, string reason)
        => Update(credentialId, state => state with
        {
            Status = ModelCredentialStatus.Invalid,
            ExhaustedUntil = null,
            InvalidReason = Truncate(reason),
        });

    /// <summary>Records a successful call, so the credentials screen can show it was used.</summary>
    public void RecordUse(string credentialId, DateTimeOffset now)
        => Update(credentialId, state => state with { LastUsedAt = now });

    private void Update(string credentialId, Func<State, State> change)
    {
        ArgumentNullException.ThrowIfNull(credentialId);

        _state.AddOrUpdate(credentialId, _ => change(State.Fresh), (_, existing) => change(existing));
    }

    private static string IdFor(string variable) => IdPrefix + variable;

    private static string? Truncate(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    private sealed record State
    {
        public static State Fresh { get; } = new();

        public ModelCredentialStatus Status { get; init; } = ModelCredentialStatus.Active;

        public DateTimeOffset? ExhaustedUntil { get; init; }

        public string? InvalidReason { get; init; }

        public DateTimeOffset? LastUsedAt { get; init; }
    }
}
