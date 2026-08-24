using Microsoft.Extensions.Configuration;
using Saga.Infrastructure.Ai;

namespace Saga.Tests;

/// <summary>
/// The flag exists so the UI can be exercised against a fully configured environment without
/// spending anything, so the case that matters most is "endpoint configured, flag on" — the one
/// a blank-endpoint check alone would get wrong.
/// </summary>
public class StandInSelectionTests
{
    private const string RealEndpoint = "https://saga-ai-mannaz.openai.azure.com/";

    private static IConfiguration Config(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    [Theory]
    // flag,   endpoint,      expected fake
    [InlineData(null, RealEndpoint, false)]  // configured and not overridden: call the real model.
    [InlineData("false", RealEndpoint, false)]
    [InlineData("true", RealEndpoint, true)]   // the override that the endpoint check alone misses.
    [InlineData(null, "", true)]               // nothing to call.
    [InlineData("false", "", true)]            // flag off still falls back on a blank endpoint.
    [InlineData("false", "   ", true)]         // whitespace is not an endpoint.
    [InlineData("true", "", true)]
    public void Ai_stand_in_is_used_when_forced_or_unconfigured(
        string? flag, string endpoint, bool expected)
    {
        var config = Config(
            ("Ai:UseFakeAi", flag),
            ("AzureOpenAI:Endpoint", endpoint));

        Assert.Equal(expected, StandInSelection.UseFakeAi(config));
    }

    [Theory]
    [InlineData(null, RealEndpoint, false)]
    [InlineData("false", RealEndpoint, false)]
    [InlineData("true", RealEndpoint, true)]
    [InlineData(null, "", true)]
    [InlineData("true", "", true)]
    public void Extractor_stand_in_is_used_when_forced_or_unconfigured(
        string? flag, string endpoint, bool expected)
    {
        var config = Config(
            ("Ai:UseFakeExtractor", flag),
            ("ContentUnderstanding:Endpoint", endpoint));

        Assert.Equal(expected, StandInSelection.UseFakeExtractor(config));
    }

    [Fact]
    public void The_two_flags_are_independent()
    {
        // Dev's actual shape: fake LLM, real document extraction against a configured resource.
        var config = Config(
            ("Ai:UseFakeAi", "true"),
            ("AzureOpenAI:Endpoint", RealEndpoint),
            ("Ai:UseFakeExtractor", "false"),
            ("ContentUnderstanding:Endpoint", RealEndpoint));

        Assert.True(StandInSelection.UseFakeAi(config));
        Assert.False(StandInSelection.UseFakeExtractor(config));
    }

    [Fact]
    public void An_empty_configuration_falls_back_to_both_stand_ins()
    {
        // A machine with no settings at all must still run the whole loop offline.
        var config = Config();

        Assert.True(StandInSelection.UseFakeAi(config));
        Assert.True(StandInSelection.UseFakeExtractor(config));
    }
}
