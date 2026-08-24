using System.ClientModel;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Ai;

/// <summary>
/// Azure OpenAI via the Azure AI Foundry resource. Authenticates with managed identity in
/// production and the developer's az login / Visual Studio credential locally; a key can be
/// configured for development before role assignments are in place.
/// </summary>
public class AzureOpenAiService : IAiService
{
    private readonly AzureOpenAIClient _client;
    private readonly string _strongDeployment;
    private readonly string _lightDeployment;

    public AzureOpenAiService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        var key = configuration["AzureOpenAI:Key"];
        _client = string.IsNullOrEmpty(key)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(key));
        _strongDeployment = configuration["AzureOpenAI:StrongDeployment"] ?? "gpt-5.6-luna";
        _lightDeployment = configuration["AzureOpenAI:LightDeployment"] ?? "gpt-5.6-luna";
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var deployment = request.Tier == AiModelTier.Light ? _lightDeployment : _strongDeployment;
        var chatClient = _client.GetChatClient(deployment);

        var messages = new List<OpenAI.Chat.ChatMessage> { new SystemChatMessage(request.SystemPrompt) };
        foreach (var message in request.Messages)
        {
            messages.Add(message.Role == "assistant"
                ? new AssistantChatMessage(message.Content)
                : new UserChatMessage(message.Content));
        }

        // Left at defaults deliberately: GPT-5.x reasoning deployments reject temperature, and the
        // usage opt-in that streaming needs is not ours to set — ChatCompletionOptions.StreamOptions
        // takes an internal type, so the SDK sends include_usage itself. If a provider ever stops
        // returning usage, UsageTrackingAiService logs it rather than banking a silent zero.
        var options = new ChatCompletionOptions();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedPromptTokens = 0;

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return new AiStreamEvent.Delta(part.Text);
            }
            if (update.Usage is not null)
            {
                promptTokens = update.Usage.InputTokenCount;
                // Already the sum of reasoning and displayed output tokens per the SDK's own docs,
                // so reasoning tokens need no separate handling — they bill as output either way.
                completionTokens = update.Usage.OutputTokenCount;
                // Cached input is billed at a fraction of the input rate, so PricingService prices
                // it separately; recorded here to keep our estimate explainable against the bill.
                cachedPromptTokens = update.Usage.InputTokenDetails?.CachedTokenCount ?? 0;
            }
        }

        yield return new AiStreamEvent.Completed(promptTokens, completionTokens, deployment, cachedPromptTokens);
    }
}
