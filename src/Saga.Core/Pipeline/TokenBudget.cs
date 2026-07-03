using Saga.Core.Domain;

namespace Saga.Core.Pipeline;

public record BudgetStatus(int EstimatedTokens, int Budget, bool OverBudget, bool UsingCondensed);

/// <summary>
/// Rough token estimation for the working-context budget meter. When material exceeds the
/// budget, generation falls back to AI-condensed versions of the documents.
/// </summary>
public static class TokenBudget
{
    public const int DefaultBudget = 100_000;

    /// <summary>~4 characters per token is a serviceable estimate across languages.</summary>
    public static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    public static BudgetStatus Assess(IReadOnlyList<Document> documents, int budget = DefaultBudget)
    {
        var fullTokens = documents.Sum(d => EstimateTokens(d.ExtractedText));
        if (fullTokens <= budget)
            return new BudgetStatus(fullTokens, budget, OverBudget: false, UsingCondensed: false);

        var condensedTokens = documents.Sum(d =>
            EstimateTokens(d.Kind == DocumentKind.Upload && d.CondensedText is not null
                ? d.CondensedText
                : d.ExtractedText));
        return new BudgetStatus(fullTokens, budget, OverBudget: true,
            UsingCondensed: documents.Any(d => d.Kind == DocumentKind.Upload && d.CondensedText is not null)
                            && condensedTokens <= budget);
    }
}
