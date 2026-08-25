using Microsoft.ML.Tokenizers;

namespace Saga.Core.Tokenization;

/// <summary>
/// Counts tokens the way the model does, for the material sizes shown in the UI and the
/// context-budget policy in <see cref="Pipeline.TokenBudget"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no published tokenizer for <c>gpt-5.6-luna</c> — it is an Azure deployment alias
/// and the model's exact BPE is not public. <c>o200k_base</c> is what the current GPT-4o /
/// GPT-5-family models encode with, so it is the right reference and lands within a few
/// percent. These are ballpark figures by design; nothing is billed from them.
/// </para>
/// <para>
/// <see cref="TiktokenTokenizer.CreateForModel"/> is deliberately not used: the model-name map
/// does not know the deployment name and would throw. The encoding name is the stable handle.
/// </para>
/// </remarks>
public static class TokenCounter
{
    // Building the tokenizer reads a few MB of embedded vocab, so it happens once, on first
    // use. TiktokenTokenizer is thread-safe for counting.
    private static readonly Lazy<TiktokenTokenizer> Tokenizer =
        new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    public static int Count(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Tokenizer.Value.CountTokens(text);
}
