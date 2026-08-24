using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
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

    private static PricingService Extraction() => Service(
        ("Pricing:ContentUnderstanding:DocumentPagesMinimalPer1000", "0.01"),
        ("Pricing:ContentUnderstanding:DocumentPagesBasicPer1000", "1.00"),
        ("Pricing:ContentUnderstanding:DocumentPagesStandardPer1000", "5.00"),
        ("Pricing:ContentUnderstanding:ContextualizationTokensPer1000", "0.001"));

    /// <summary>
    /// The whole point of the meter split: the same analyzer charges 500× more for a page of a
    /// scanned PDF than for a page of a .docx, so one blended rate could only ever be wrong for one
    /// of them. A tender and the screenshots inside it hit both meters on the same upload.
    /// </summary>
    [Fact]
    public void Each_extraction_meter_is_priced_at_its_own_rate()
    {
        var pricing = Extraction();

        // 1000 minimal pages at 0.01/1000.
        Assert.Equal(0.01m, pricing.EstimateExtractionUsd(new ExtractionUsage(1000, 0, 0)));
        // 20 basic pages at 1.00/1000.
        Assert.Equal(0.02m, pricing.EstimateExtractionUsd(new ExtractionUsage(0, 20, 0)));
        // A single standard page — one screenshot lifted out of a .docx — at 5.00/1000.
        Assert.Equal(0.005m, pricing.EstimateExtractionUsd(new ExtractionUsage(0, 0, 1)));
    }

    [Fact]
    public void A_call_spanning_several_meters_sums_them()
    {
        // 10 minimal (0.0001) + 4 basic (0.004) + 2 standard (0.01) + 3000 tokens (0.003).
        Assert.Equal(0.0171m, Extraction().EstimateExtractionUsd(new ExtractionUsage(10, 4, 2, 3000)));
    }

    /// <summary>
    /// Unknown is not free. The service reporting nothing means we cannot say what it cost, and the
    /// caller warns about it — but pricing still may not throw in the middle of an upload.
    /// </summary>
    [Fact]
    public void Unreported_usage_costs_zero_rather_than_throwing()
    {
        Assert.Equal(0m, Extraction().EstimateExtractionUsd(null));
        Assert.Equal(0m, Extraction().EstimateExtractionUsd(ExtractionUsage.Free));
    }

    [Fact]
    public void An_unpriced_meter_costs_zero_without_dragging_down_the_priced_ones()
    {
        // Only Standard is configured; Minimal pages are unpriced and must not throw.
        var pricing = Service(("Pricing:ContentUnderstanding:DocumentPagesStandardPer1000", "5.00"));

        Assert.Equal(0m, pricing.EstimateExtractionUsd(new ExtractionUsage(1000, 0, 0)));
        Assert.Equal(0.01m, pricing.EstimateExtractionUsd(new ExtractionUsage(1000, 0, 2)));
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
