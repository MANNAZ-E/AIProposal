using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

    public decimal EstimateLlmUsd(string model, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0m;

        var input = configuration.GetValue<decimal>($"Pricing:Models:{model}:InputPer1M");
        var output = configuration.GetValue<decimal>($"Pricing:Models:{model}:OutputPer1M");
        if (input == 0m && output == 0m) WarnOnce(model);

        return (inputTokens * input + outputTokens * output) / 1_000_000m;
    }

    public decimal EstimateExtractionUsd(string analyzerId, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(analyzerId) || pageCount <= 0) return 0m;

        var per1000 = configuration.GetValue<decimal>($"Pricing:ContentUnderstanding:{analyzerId}:Per1000Pages");
        if (per1000 == 0m) WarnOnce(analyzerId);

        return pageCount * per1000 / 1000m;
    }

    /// <summary>Once per model per process — an unpriced model would otherwise log on every call.</summary>
    private void WarnOnce(string key)
    {
        if (_warned.TryAdd(key, 0))
            logger?.LogWarning("No price configured for '{Key}'; its usage will be recorded at zero cost.", key);
    }
}
