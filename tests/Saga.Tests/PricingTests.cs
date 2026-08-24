using Microsoft.Extensions.Configuration;
using Saga.Infrastructure.Ai;

namespace Saga.Tests;

/// <summary>Rates are configured per model in USD; unknown models must never throw.</summary>
public class PricingTests
{
    private static PricingService Service(params (string Key, string Value)[] settings)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void Llm_cost_comes_from_the_model_keyed_rates()
    {
        var pricing = Service(
            ("Pricing:Models:gpt-5.6-terra:InputPer1M", "1.25"),
            ("Pricing:Models:gpt-5.6-terra:OutputPer1M", "10"));

        // 200k input at 1.25/1M = 0.25; 40k output at 10/1M = 0.40.
        Assert.Equal(0.65m, pricing.EstimateLlmUsd("gpt-5.6-terra", 200_000, 40_000));
    }

    [Fact]
    public void Each_model_is_priced_separately()
    {
        var pricing = Service(
            ("Pricing:Models:gpt-5.6-terra:InputPer1M", "1.25"),
            ("Pricing:Models:gpt-5.6-terra:OutputPer1M", "10"),
            ("Pricing:Models:gpt-5.6-luna:InputPer1M", "0.25"),
            ("Pricing:Models:gpt-5.6-luna:OutputPer1M", "2"));

        Assert.Equal(0.65m, pricing.EstimateLlmUsd("gpt-5.6-terra", 200_000, 40_000));
        Assert.Equal(0.13m, pricing.EstimateLlmUsd("gpt-5.6-luna", 200_000, 40_000));
    }

    [Fact]
    public void Cached_input_is_billed_at_the_cached_rate()
    {
        var pricing = Service(
            ("Pricing:Models:gpt-5.6-terra:InputPer1M", "4.40"),
            ("Pricing:Models:gpt-5.6-terra:CachedInputPer1M", "0.44"),
            ("Pricing:Models:gpt-5.6-terra:OutputPer1M", "26.40"));

        // 100k input of which 80k cached: 20k at 4.40/1M = 0.088, 80k at 0.44/1M = 0.0352;
        // 10k output at 26.40/1M = 0.264. The same call with no cache would be 0.704.
        Assert.Equal(0.3872m, pricing.EstimateLlmUsd("gpt-5.6-terra", 100_000, 10_000, 80_000));
        Assert.Equal(0.704m, pricing.EstimateLlmUsd("gpt-5.6-terra", 100_000, 10_000));

        // A cached count at or above the input total must not make the uncached remainder negative.
        Assert.Equal(0.308m, pricing.EstimateLlmUsd("gpt-5.6-terra", 100_000, 10_000, 100_000));
        Assert.Equal(0.308m, pricing.EstimateLlmUsd("gpt-5.6-terra", 100_000, 10_000, 250_000));
    }

    [Fact]
    public void A_missing_cached_rate_falls_back_to_the_full_input_rate()
    {
        // Omitting CachedInputPer1M must price exactly as before the cached rate existed, so an
        // existing config is never silently reinterpreted.
        var pricing = Service(
            ("Pricing:Models:gpt-5.6-terra:InputPer1M", "1.25"),
            ("Pricing:Models:gpt-5.6-terra:OutputPer1M", "10"));

        Assert.Equal(0.65m, pricing.EstimateLlmUsd("gpt-5.6-terra", 200_000, 40_000, 150_000));
        Assert.Equal(pricing.EstimateLlmUsd("gpt-5.6-terra", 200_000, 40_000),
            pricing.EstimateLlmUsd("gpt-5.6-terra", 200_000, 40_000, 150_000));
    }

    [Fact]
    public void An_unpriced_model_costs_zero_rather_than_throwing()
    {
        var pricing = Service(("Pricing:Models:gpt-5.6-terra:InputPer1M", "1.25"));

        // Metering must never break the generation it is measuring.
        Assert.Equal(0m, pricing.EstimateLlmUsd("some-new-deployment", 200_000, 40_000));
        Assert.Equal(0m, pricing.EstimateLlmUsd("", 200_000, 40_000));
    }

    [Fact]
    public void Extraction_is_priced_per_thousand_pages()
    {
        var pricing = Service(("Pricing:ContentUnderstanding:prebuilt-layout:Per1000Pages", "10"));

        Assert.Equal(0.12m, pricing.EstimateExtractionUsd("prebuilt-layout", 12));
        Assert.Equal(0m, pricing.EstimateExtractionUsd("prebuilt-layout", 0));
        Assert.Equal(0m, pricing.EstimateExtractionUsd("unknown-analyzer", 12));
    }

    [Fact]
    public void Display_rate_and_payload_capture_default_sensibly()
    {
        var unset = Service();
        Assert.Equal(0m, unset.UsdToDkk);        // 0 means the UI shows USD unconverted.
        Assert.True(unset.CapturePayloads);      // Capture is on unless explicitly disabled.

        var configured = Service(("Pricing:UsdToDkk", "6.9"), ("Pricing:CapturePayloads", "false"));
        Assert.Equal(6.9m, configured.UsdToDkk);
        Assert.False(configured.CapturePayloads);
    }
}
