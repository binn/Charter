using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Microsoft.EntityFrameworkCore;

namespace Charter.Budgets;

/// <summary>How a figure was arrived at, so nothing downstream treats a guess as a quote.</summary>
public enum BudgetEstimateBasis
{
    /// <summary>
    /// No price for the model. The figure is zero and means <em>unknown</em>, never <em>free</em>.
    /// </summary>
    Unpriced,

    /// <summary>Spec scope multiplied by the model's published per-token price.</summary>
    Priced,

    /// <summary>The running distribution of what similar work actually cost in this repository.</summary>
    Historical,
}

/// <summary>What a piece of work is expected to cost, before it runs (section 34.4 step 1).</summary>
public sealed record BudgetEstimate
{
    /// <summary>Which currency the work is denominated in (section 34.1).</summary>
    public required LedgerUnit Unit { get; init; }

    /// <summary>Real marginal dollars. Zero for subscription-backed work.</summary>
    public decimal Usd { get; init; }

    /// <summary>Quota consumed against the credential owner. Zero for metered work.</summary>
    public decimal QuotaSessions { get; init; }

    /// <summary>
    /// What the same work would cost on a metered API. Equals <see cref="Usd"/> for metered spend
    /// and makes a subscription session visible as something other than <c>$0.00</c> (section 20b.5).
    /// </summary>
    public decimal ImputedUsd { get; init; }

    /// <summary>How the figure was arrived at.</summary>
    public required BudgetEstimateBasis Basis { get; init; }

    /// <summary>How many past sessions the historical figure averaged. Zero when it did not.</summary>
    public int SampleSize { get; init; }

    /// <summary>The token counts behind a priced estimate, for the record.</summary>
    public long InputTokens { get; init; }

    /// <summary>The token counts behind a priced estimate, for the record.</summary>
    public long OutputTokens { get; init; }

    /// <summary>The amount a budget denominated in <paramref name="unit"/> would be debited.</summary>
    /// <remarks>
    /// A subscription session debits nothing from a dollar budget, because it costs nothing in
    /// dollars — that is the whole reason section 34.1 refuses to conflate the two units. It still
    /// shows up in reporting at <see cref="ImputedUsd"/>.
    /// </remarks>
    public decimal AmountIn(LedgerUnit unit) => unit == LedgerUnit.Usd ? Usd : QuotaSessions;

    /// <summary>An estimate of nothing, for work with no priced model behind it.</summary>
    public static BudgetEstimate Free { get; } = new()
    {
        Unit = LedgerUnit.Usd,
        Basis = BudgetEstimateBasis.Unpriced,
    };
}

/// <summary>What to estimate.</summary>
public sealed record BudgetEstimateRequest
{
    /// <summary>The organisation.</summary>
    public required Guid OrgId { get; init; }

    /// <summary>The repository, where the work has one. History is kept per repository.</summary>
    public Guid? RepoId { get; init; }

    /// <summary>Which cost line this is (section 34.6).</summary>
    public required LedgerCategory Category { get; init; }

    /// <summary>The model, provider-qualified (section 20b.1).</summary>
    public required string Model { get; init; }

    /// <summary>True when a subscription grant will serve the work (section 20b.5).</summary>
    public bool SubscriptionBacked { get; init; }

    /// <summary>The approved specification's body, for scope. Absent for chat and recon.</summary>
    public string? SpecBodyMd { get; init; }

    /// <summary>How many acceptance criteria the spec carries.</summary>
    public int AcceptanceCriteria { get; init; }
}

/// <summary>Estimates what a piece of work will cost before it is dispatched (section 34.4).</summary>
public interface IBudgetEstimator
{
    /// <summary>Estimates one piece of work.</summary>
    Task<BudgetEstimate> EstimateAsync(BudgetEstimateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Section 34.4 step 1: <em>estimate before dispatch — from the spec's scope, historical cost for
/// similar work in this repo, and the selected model's price</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a coarse instrument and is meant to be.</strong> The number it produces is a hold,
/// not a quote, and the design that makes the hold safe is the settlement immediately after it
/// (section 34.4 step 3), which replaces the estimate with what actually happened and releases the
/// difference. An estimator that is right to the dollar and a budget with no holds is a cap that ten
/// concurrent sessions still blow through; a rough estimator with holds is not.
/// </para>
/// <para>
/// Two paths. With enough settled sessions for the same repository and category, the median actual
/// is used — the median rather than the mean because one runaway session should not move every
/// subsequent estimate, and a distribution of agent-run costs has a long right tail. Below that, the
/// spec's scope is turned into token counts and priced from
/// <see cref="IModelPriceCatalog"/>. Both paths are then scaled by the same scope factor, centred on
/// 1.0 for a spec of ordinary size, so a five-line change and a rewrite do not reserve the same hold.
/// </para>
/// <para>
/// An unpriced model — a self-hosted endpoint, or one no catalog knows — estimates zero with
/// <see cref="BudgetEstimateBasis.Unpriced"/>. That is the honest answer and it is also a hole:
/// a budget cannot govern spend nobody can price, and <see cref="BudgetDecision"/> says so in the
/// message rather than letting the work look free. See <c>docs/budgets.md</c>.
/// </para>
/// </remarks>
public sealed class BudgetEstimator : IBudgetEstimator
{
    /// <summary>
    /// Starting token counts per category, before the spec's scope is applied.
    /// </summary>
    /// <remarks>
    /// Section 34.6 is the reason these differ by an order of magnitude rather than by a fudge
    /// factor: a build reads a repository and writes code, and a chat answers a question. Sized from
    /// the shape of each pass — how much context Charter sends and how much the model writes back —
    /// not measured, which is exactly why the historical path exists to replace them.
    /// </remarks>
    private static readonly Dictionary<LedgerCategory, (long Input, long Output)> BaseTokens = new()
    {
        [LedgerCategory.Build] = (120_000, 30_000),
        [LedgerCategory.Scaffold] = (60_000, 20_000),
        [LedgerCategory.Recon] = (40_000, 4_000),
        [LedgerCategory.Recap] = (20_000, 2_500),
        [LedgerCategory.Teach] = (15_000, 2_000),
        [LedgerCategory.Refine] = (12_000, 3_000),
        [LedgerCategory.Chat] = (6_000, 1_000),
    };

    /// <summary>A spec of ordinary size, in characters and acceptance criteria.</summary>
    private const int TypicalSpecCharacters = 4_000;

    private const int TypicalAcceptanceCriteria = 3;

    private readonly CharterDbContext _db;
    private readonly IModelPriceCatalog _prices;
    private readonly BudgetOptions _options;

    /// <summary>Creates an estimator.</summary>
    public BudgetEstimator(CharterDbContext db, IModelPriceCatalog prices, BudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _prices = prices;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<BudgetEstimate> EstimateAsync(
        BudgetEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ScopeFactor(request);

        var history = await HistoryAsync(request, cancellationToken).ConfigureAwait(false);
        if (history is { Count: > 0 } samples && samples.Count >= _options.MinimumHistorySamples)
        {
            return Build(request, Median(samples) * scope, BudgetEstimateBasis.Historical, samples.Count, 0, 0);
        }

        var (input, output) = Tokens(request.Category, scope);

        var model = ModelIdentifier.Parse(request.Model);
        var price = await _prices.TryGetPriceAsync(model, cancellationToken).ConfigureAwait(false);

        if (price is not { } known)
        {
            return request.SubscriptionBacked
                ? BudgetEstimate.Free with { Unit = LedgerUnit.QuotaSessions, QuotaSessions = 1m }
                : BudgetEstimate.Free;
        }

        var usage = new ModelUsage { InputTokens = input, OutputTokens = output };

        return Build(
            request,
            ModelCostCalculator.Estimate(usage, known),
            BudgetEstimateBasis.Priced,
            sampleSize: 0,
            input,
            output);
    }

    /// <summary>
    /// How much bigger or smaller than an ordinary piece of work this one is.
    /// </summary>
    /// <remarks>
    /// Centred on 1.0 and clamped, because the two inputs available before dispatch — how long the
    /// spec is and how many acceptance criteria it carries — correlate with cost loosely enough that
    /// an unclamped multiplier would produce confident nonsense at both ends.
    /// </remarks>
    public static decimal ScopeFactor(BudgetEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var characters = request.SpecBodyMd?.Length ?? 0;
        if (characters == 0 && request.AcceptanceCriteria == 0)
        {
            return 1m;
        }

        var lengthTerm = ((decimal)characters / TypicalSpecCharacters) - 1m;
        var criteriaTerm = (decimal)(request.AcceptanceCriteria - TypicalAcceptanceCriteria);

        var factor = 1m + (lengthTerm * 0.15m) + (criteriaTerm * 0.12m);

        return Math.Clamp(factor, 0.5m, 3m);
    }

    private static (long Input, long Output) Tokens(LedgerCategory category, decimal scope)
    {
        var (input, output) = BaseTokens.TryGetValue(category, out var known) ? known : (20_000L, 4_000L);

        return ((long)(input * scope), (long)(output * scope));
    }

    private static BudgetEstimate Build(
        BudgetEstimateRequest request,
        decimal imputedUsd,
        BudgetEstimateBasis basis,
        int sampleSize,
        long input,
        long output)
    {
        var rounded = decimal.Round(Math.Max(0m, imputedUsd), 4, MidpointRounding.AwayFromZero);

        return new BudgetEstimate
        {
            Unit = request.SubscriptionBacked ? LedgerUnit.QuotaSessions : LedgerUnit.Usd,

            // Section 20b.5: a subscription-backed session has no marginal cost and still consumes
            // scarce quota. Charging it in dollars would let a quota budget go unenforced and a
            // dollar budget be spent twice.
            Usd = request.SubscriptionBacked ? 0m : rounded,
            QuotaSessions = request.SubscriptionBacked ? 1m : 0m,
            ImputedUsd = rounded,
            Basis = basis,
            SampleSize = sampleSize,
            InputTokens = input,
            OutputTokens = output,
        };
    }

    /// <summary>
    /// What similar work actually cost in this repository, most recent first.
    /// </summary>
    /// <remarks>
    /// Imputed dollars rather than real ones, so a repository whose sessions run on a subscription
    /// still trains the estimator instead of teaching it that everything is free.
    /// </remarks>
    private async Task<List<decimal>?> HistoryAsync(
        BudgetEstimateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RepoId is not { } repoId)
        {
            return null;
        }

        return await (
            from entry in _db.LedgerEntries.AsNoTracking()
            where entry.OrgId == request.OrgId
                && entry.State == LedgerState.Settled
                && entry.Category == request.Category
            join session in _db.Sessions.AsNoTracking() on entry.SessionId equals session.Id
            join spec in _db.Specs.AsNoTracking() on session.SpecId equals spec.Id
            join req in _db.Requests.AsNoTracking() on spec.RequestId equals req.Id
            where req.RepoId == repoId
            orderby entry.CreatedAt descending
            select entry.ImputedUsd)
            .Take(_options.HistorySampleSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();

        var middle = values.Count / 2;

        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2m;
    }
}
