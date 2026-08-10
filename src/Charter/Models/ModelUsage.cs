namespace Charter.Models;

/// <summary>Token counts for one model invocation.</summary>
/// <remarks>
/// Reported on every response. Sections 20b.5 and 34.4 both consume it - the ledger settles against
/// actual usage and the budget estimator reserves against it - so it is not optional per call.
/// </remarks>
public sealed record ModelUsage
{
    /// <summary>Uncached prompt tokens.</summary>
    public long InputTokens { get; init; }

    /// <summary>Generated tokens, including reasoning tokens where the provider bills them as output.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Prompt tokens served from a provider-side cache, billed at a reduced rate.</summary>
    public long CacheReadInputTokens { get; init; }

    /// <summary>Prompt tokens written to a provider-side cache, billed at a premium.</summary>
    public long CacheWriteInputTokens { get; init; }

    /// <summary>
    /// Cost in USD as reported by the provider itself, when it reports one. OpenRouter does when
    /// usage accounting is requested (section 20b.6); most providers do not.
    /// </summary>
    public decimal? ProviderReportedCostUsd { get; init; }

    /// <summary>Every prompt token, cached or not.</summary>
    public long TotalInputTokens => InputTokens + CacheReadInputTokens + CacheWriteInputTokens;

    /// <summary>An empty usage record, for responses that carried none.</summary>
    public static ModelUsage Empty { get; } = new();

    /// <summary>Adds two usage records, for accumulating across a streamed response.</summary>
    public static ModelUsage operator +(ModelUsage left, ModelUsage right) => Add(left, right);

    /// <summary>Adds two usage records, for accumulating across a streamed response.</summary>
    public static ModelUsage Add(ModelUsage left, ModelUsage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return new ModelUsage
        {
            InputTokens = left.InputTokens + right.InputTokens,
            OutputTokens = left.OutputTokens + right.OutputTokens,
            CacheReadInputTokens = left.CacheReadInputTokens + right.CacheReadInputTokens,
            CacheWriteInputTokens = left.CacheWriteInputTokens + right.CacheWriteInputTokens,
            ProviderReportedCostUsd = (left.ProviderReportedCostUsd, right.ProviderReportedCostUsd) switch
            {
                (null, null) => null,
                (var a, null) => a,
                (null, var b) => b,
                var (a, b) => a + b,
            },
        };
    }
}

/// <summary>
/// The unit a call is charged in. Section 20b.5: ledger units are not all dollars, and reporting a
/// subscription-backed session as <c>$0.00</c> makes budget dashboards lie.
/// </summary>
public enum ModelChargeUnit
{
    /// <summary>A metered grant. The call has a real marginal dollar cost.</summary>
    Usd,

    /// <summary>
    /// A subscription grant. The call has no marginal cost but consumes scarce quota belonging to
    /// <see cref="ModelCharge.OwnerUserId"/>.
    /// </summary>
    SubscriptionQuota,
}

/// <summary>How a dollar figure was arrived at.</summary>
public enum ModelCostBasis
{
    /// <summary>No price was available for the model. The figure is zero and means "unknown".</summary>
    Unpriced,

    /// <summary>Derived from a price table or the OpenRouter catalog multiplied by token counts.</summary>
    Estimated,

    /// <summary>Reported by the provider on the response itself. Preferred over an estimate.</summary>
    ProviderReported,
}

/// <summary>
/// What one invocation cost, and to whom. Section 20b.5 requires tracking both currencies, and
/// attribution back to the grant owner so a shared-pool owner can see who spent their quota.
/// </summary>
public sealed record ModelCharge
{
    /// <summary>Which currency this charge is denominated in.</summary>
    public required ModelChargeUnit Unit { get; init; }

    /// <summary>
    /// The metered dollar cost. Zero for a subscription grant, where the ledger unit is quota rather
    /// than dollars - see <see cref="NotionalCostUsd"/> for what the same tokens would have cost.
    /// </summary>
    public decimal CostUsd { get; init; }

    /// <summary>
    /// What the call would have cost at metered rates, whether or not it was actually billed. Shown
    /// alongside quota consumption so a subscription session is not reported as free.
    /// </summary>
    public decimal NotionalCostUsd { get; init; }

    /// <summary>How <see cref="CostUsd"/> and <see cref="NotionalCostUsd"/> were arrived at.</summary>
    public required ModelCostBasis Basis { get; init; }

    /// <summary>The grant this call was billed against.</summary>
    public string? CredentialId { get; init; }

    /// <summary>
    /// The person whose credential paid for the call. For a shared-pool grant this is not the
    /// requester, which is exactly why section 20b.5 requires it be recorded.
    /// </summary>
    public string? OwnerUserId { get; init; }

    /// <summary>An unpriced, unattributed charge.</summary>
    public static ModelCharge None { get; } = new()
    {
        Unit = ModelChargeUnit.Usd,
        Basis = ModelCostBasis.Unpriced,
    };
}

/// <summary>A completed control-plane model invocation.</summary>
public sealed record ModelCompletion
{
    /// <summary>The model that actually served the request.</summary>
    public required ModelIdentifier Model { get; init; }

    /// <summary>The response text, with all text blocks concatenated.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The structured payload, when <see cref="ModelRequest.ResponseFormat"/> was set. Normally the
    /// same JSON as <see cref="Text"/>; kept separate so callers do not have to guess.
    /// </summary>
    public string? StructuredJson { get; init; }

    /// <summary>Why generation stopped.</summary>
    public ModelStopReason StopReason { get; init; }

    /// <summary>Token counts for the call.</summary>
    public required ModelUsage Usage { get; init; }

    /// <summary>What the call cost, and against whom.</summary>
    public required ModelCharge Charge { get; init; }
}
