using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Ai;

/// <summary>
/// Turns metered units into money. Rates are configured in <b>USD</b> — the currency Microsoft
/// publishes Azure list prices in, so they can be copied across without arithmetic — and are
/// keyed by model/deployment name rather than by tier, since the service will run more than the
/// two deployments the old Strong/Light split assumed.
/// </summary>
/// <remarks>
/// A missing rate yields 0 rather than throwing: metering must never break a generation.
/// </remarks>
public class PricingService(IConfiguration configuration, ILogger<PricingService>? logger = null)
{
    private readonly ConcurrentDictionary<string, byte> _warned = new();

    /// <summary>Display rate for DKK. 0 or unset means the UI shows USD unconverted.</summary>
    public decimal UsdToDkk => configuration.GetValue<decimal>("Pricing:UsdToDkk");

    /// <summary>Whether request/response payloads are stored. Off is the escape hatch if the table grows.</summary>
    public bool CapturePayloads => configuration.GetValue("Pricing:CapturePayloads", true);

    /// <param name="cachedInputTokens">
    /// Prompt tokens the provider served from its cache, billed at <c>CachedInputPer1M</c> — a
    /// fraction of the full input rate. Part of <paramref name="inputTokens"/>, not additional to
    /// it. Matters here because the system prompt and working context repeat across every call of
    /// a run. A model with no cached rate configured falls back to the full input rate, so leaving
    /// the key out prices exactly as before.
    /// </param>
    public decimal EstimateLlmUsd(string model, int inputTokens, int outputTokens,
        int cachedInputTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0m;

        var input = configuration.GetValue<decimal>($"Pricing:Models:{model}:InputPer1M");
        var output = configuration.GetValue<decimal>($"Pricing:Models:{model}:OutputPer1M");
        if (input == 0m && output == 0m) WarnOnce(model);

        var cachedRate = configuration.GetValue<decimal>($"Pricing:Models:{model}:CachedInputPer1M");
        if (cachedRate == 0m) cachedRate = input;

        // Clamped because the cached count is reported by the provider: a value above the total
        // input would otherwise make the uncached remainder negative.
        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(inputTokens, 0));
        var uncached = Math.Max(inputTokens, 0) - cached;

        return (uncached * input + cached * cachedRate + outputTokens * output) / 1_000_000m;
    }

    /// <summary>
    /// Prices what Content Understanding reported it billed. Rates are per 1,000 units and keyed by
    /// <b>meter</b>, not by analyzer: the service charges for the work actually performed, so one
    /// analyzer bills Minimal on a digital Office file and Standard on a PDF or a screenshot.
    /// A null usage — the service told us nothing — costs 0 here and is warned about at the call site.
    /// </summary>
    public decimal EstimateExtractionUsd(ExtractionUsage? usage)
    {
        if (usage is null) return 0m;

        return Units(usage.MinimalPages, "DocumentPagesMinimalPer1000")
            + Units(usage.BasicPages, "DocumentPagesBasicPer1000")
            + Units(usage.StandardPages, "DocumentPagesStandardPer1000")
            + Units(usage.ContextualizationTokens, "ContextualizationTokensPer1000");
    }

    /// <summary>
    /// Warns per meter rather than per analyzer — with several meters under one analyzer, a single
    /// shared warning key would let a priced meter silence a missing one. Only a meter that actually
    /// consumed something is worth complaining about.
    /// </summary>
    private decimal Units(int quantity, string key)
    {
        if (quantity <= 0) return 0m;

        var per1000 = configuration.GetValue<decimal>($"Pricing:ContentUnderstanding:{key}");
        if (per1000 == 0m) WarnOnce($"ContentUnderstanding:{key}");

        return quantity * per1000 / 1000m;
    }

    /// <summary>
    /// Once per key per process — an unpriced model or meter would otherwise log on every call.
    /// The key is a deployment name for LLM rates and a meter for extraction rates, never an
    /// analyzer: several meters share one analyzer, and a shared key would let a priced meter
    /// silence a missing one.
    /// </summary>
    private void WarnOnce(string key)
    {
        if (_warned.TryAdd(key, 0))
            logger?.LogWarning("No price configured for '{Key}'; its usage will be recorded at zero cost.", key);
    }
}
