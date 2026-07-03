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
        _strongDeployment = configuration["AzureOpenAI:StrongDeployment"] ?? "gpt-5.4";
        _lightDeployment = configuration["AzureOpenAI:LightDeployment"] ?? "gpt-5.4-mini";
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

        var options = new ChatCompletionOptions();
        var promptTokens = 0;
        var completionTokens = 0;

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
                completionTokens = update.Usage.OutputTokenCount;
            }
        }

        yield return new AiStreamEvent.Completed(promptTokens, completionTokens, deployment);
    }
}
