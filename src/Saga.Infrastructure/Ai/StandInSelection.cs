using Microsoft.Extensions.Configuration;

namespace Saga.Infrastructure.Ai;

/// <summary>
/// Decides whether a paid dependency is replaced by its offline stand-in. Lives here rather than
/// inline in <c>Program.cs</c> so the rule is testable: getting it wrong either bills real money
/// during UI testing or serves canned text in production, and neither announces itself.
/// </summary>
public static class StandInSelection
{
    /// <summary>
    /// True when the LLM should be the offline stand-in: either <c>Ai:UseFakeAi</c> is set — which
    /// forces the fake even with a real endpoint configured, the escape hatch for exercising the UI
    /// without spending tokens — or there is no endpoint to call in the first place.
    /// </summary>
    public static bool UseFakeAi(IConfiguration configuration)
        => configuration.GetValue("Ai:UseFakeAi", false)
            || string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]);

    /// <summary>
    /// True when document extraction should be the offline stand-in. Separate from
    /// <see cref="UseFakeAi"/> on purpose: dev points at a real Content Understanding resource, so
    /// uploads bill for real unless this is turned on independently of the LLM.
    /// </summary>
    public static bool UseFakeExtractor(IConfiguration configuration)
        => configuration.GetValue("Ai:UseFakeExtractor", false)
            || string.IsNullOrWhiteSpace(configuration["ContentUnderstanding:Endpoint"]);
}
