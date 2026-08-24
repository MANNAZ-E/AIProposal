using Microsoft.Extensions.Configuration;
using Saga.Infrastructure.Ai;

namespace Saga.Tests;

/// <summary>
/// Guards the shipped appsettings files rather than hand-built configuration. Rates are keyed by
/// deployment name, and the key that gets looked up is whatever the model reports back — so a
/// deployment renamed without renaming its price key does not fail loudly, it silently records
/// every call at zero cost. These tests are the thing that notices.
/// </summary>
public class PricingConfigurationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Saga.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir); // The tests must be running from inside the repo.
        return dir!.FullName;
    }

    /// <summary>Loads the real file, so a JSON error or a stray comment fails here too.</summary>
    private static IConfigurationRoot Load(params string[] fileNames)
    {
        var builder = new ConfigurationBuilder();
        foreach (var name in fileNames)
        {
            var path = Path.Combine(RepoRoot(), "src", "Saga.Web", name);
            Assert.True(File.Exists(path), $"{name} not found at {path}");
            builder.AddJsonFile(path, optional: false);
        }
        return builder.Build();
    }

    public static TheoryData<string> ConfigFiles => new()
    {
        "appsettings.json",
        "appsettings.Development.json",
    };

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Every_configured_deployment_has_a_matching_price_key(string fileName)
    {
        var config = Load(fileName);
        var pricing = new PricingService(config);

        foreach (var setting in new[] { "AzureOpenAI:StrongDeployment", "AzureOpenAI:LightDeployment" })
        {
            var deployment = config[setting];
            Assert.False(string.IsNullOrWhiteSpace(deployment), $"{setting} is not set in {fileName}.");

            // 1M input tokens: a priced model cannot come back at zero, so this fails on a typo,
            // a rename, or a price block that was never filled in.
            var cost = pricing.EstimateLlmUsd(deployment!, 1_000_000, 0);
            Assert.True(cost > 0m,
                $"{fileName}: no usable rate for '{deployment}' ({setting}). Add "
                + $"Pricing:Models:{deployment}:InputPer1M — otherwise its calls record at zero cost.");
        }
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Cached_input_is_cheaper_than_uncached_input_for_every_priced_model(string fileName)
    {
        var config = Load(fileName);
        var pricing = new PricingService(config);

        foreach (var deployment in new[] { config["AzureOpenAI:StrongDeployment"]!,
                                           config["AzureOpenAI:LightDeployment"]! })
        {
            var uncached = pricing.EstimateLlmUsd(deployment, 1_000_000, 0);
            var cached = pricing.EstimateLlmUsd(deployment, 1_000_000, 0, 1_000_000);

            Assert.True(cached < uncached,
                $"{fileName}: '{deployment}' prices cached input at the full rate "
                + $"({cached} vs {uncached}). Set Pricing:Models:{deployment}:CachedInputPer1M.");
        }
    }

    [Fact]
    public void Development_still_prices_the_offline_stand_in()
    {
        // The fake reports model "fake-model"; without a rate, local usage pages read all zeros
        // and stop being a check on anything.
        var pricing = new PricingService(Load("appsettings.Development.json"));

        Assert.True(pricing.EstimateLlmUsd("fake-model", 1_000_000, 1_000_000) > 0m);
    }

    [Fact]
    public void Production_does_not_ship_the_offline_stand_ins_switched_on()
    {
        // Either flag left true in appsettings.json would serve canned text in production.
        var config = Load("appsettings.json");

        Assert.False(config.GetValue("Ai:UseFakeAi", false));
        Assert.False(config.GetValue("Ai:UseFakeExtractor", false));
    }
}
