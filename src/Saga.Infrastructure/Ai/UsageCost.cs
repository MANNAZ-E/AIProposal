using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Ai;

public static class UsageCost
{
    /// <summary>Prices per million tokens from configuration; 0 until configured.</summary>
    public static decimal Estimate(IConfiguration configuration, AiModelTier tier,
        int promptTokens, int completionTokens)
    {
        var prefix = tier == AiModelTier.Light ? "AzureOpenAI:LightPrice" : "AzureOpenAI:StrongPrice";
        var inputPer1M = configuration.GetValue<decimal>($"{prefix}:InputPer1M");
        var outputPer1M = configuration.GetValue<decimal>($"{prefix}:OutputPer1M");
        return (promptTokens * inputPer1M + completionTokens * outputPer1M) / 1_000_000m;
    }
}
