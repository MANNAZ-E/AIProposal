namespace Saga.Core.Abstractions;

/// <summary>Which model deployment to use: Strong (GPT 5.4) or Light (GPT 5.4 mini, extraction).</summary>
public enum AiModelTier
{
    Strong = 0,
    Light = 1,
}

public record AiMessage(string Role, string Content)
{
    public static AiMessage User(string content) => new("user", content);
    public static AiMessage Assistant(string content) => new("assistant", content);
}

/// <param name="Context">Attribution for usage logging; null means the call is not metered.</param>
public record AiRequest(
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    AiModelTier Tier = AiModelTier.Strong,
    AiCallContext? Context = null);

public abstract record AiStreamEvent
{
    /// <summary>A chunk of generated text.</summary>
    public sealed record Delta(string Text) : AiStreamEvent;

    /// <summary>Emitted once at the end with usage for cost logging.</summary>
    /// <param name="CachedPromptTokens">Prompt tokens served from the provider's cache, where reported.</param>
    public sealed record Completed(int PromptTokens, int CompletionTokens, string Model,
        int CachedPromptTokens = 0) : AiStreamEvent;
}

public interface IAiService
{
    IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken ct = default);
}

public record AiCompletion(string Text, int PromptTokens, int CompletionTokens, string Model);

public static class AiServiceExtensions
{
    /// <summary>Runs a request to completion, concatenating deltas; for non-interactive calls.</summary>
    public static async Task<AiCompletion> CompleteAsync(this IAiService ai, AiRequest request,
        CancellationToken ct = default)
    {
        var text = new System.Text.StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var model = "";
        await foreach (var evt in ai.StreamAsync(request, ct))
        {
            switch (evt)
            {
                case AiStreamEvent.Delta d:
                    text.Append(d.Text);
                    break;
                case AiStreamEvent.Completed c:
                    (promptTokens, completionTokens, model) = (c.PromptTokens, c.CompletionTokens, c.Model);
                    break;
            }
        }
        return new AiCompletion(text.ToString(), promptTokens, completionTokens, model);
    }
}
