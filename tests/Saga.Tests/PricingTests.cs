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
            ("Pricing:Models:gpt-5.4:InputPer1M", "1.25"),
            ("Pricing:Models:gpt-5.4:OutputPer1M", "10"));

        // 200k input at 1.25/1M = 0.25; 40k output at 10/1M = 0.40.
        Assert.Equal(0.65m, pricing.EstimateLlmUsd("gpt-5.4", 200_000, 40_000));
    }

    [Fact]
    public void Each_model_is_priced_separately()
    {
        var pricing = Service(
            ("Pricing:Models:gpt-5.4:InputPer1M", "1.25"),
            ("Pricing:Models:gpt-5.4:OutputPer1M", "10"),
            ("Pricing:Models:gpt-5.4-mini:InputPer1M", "0.25"),
            ("Pricing:Models:gpt-5.4-mini:OutputPer1M", "2"));

        Assert.Equal(0.65m, pricing.EstimateLlmUsd("gpt-5.4", 200_000, 40_000));
        Assert.Equal(0.13m, pricing.EstimateLlmUsd("gpt-5.4-mini", 200_000, 40_000));
    }

    [Fact]
    public void An_unpriced_model_costs_zero_rather_than_throwing()
    {
        var pricing = Service(("Pricing:Models:gpt-5.4:InputPer1M", "1.25"));

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
