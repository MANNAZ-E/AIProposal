using Saga.Core.Domain;
using Saga.Core.Tokenization;

namespace Saga.Core.Pipeline;

public record BudgetStatus(int Tokens, int Budget, bool OverBudget, bool UsingCondensed);

/// <summary>
/// The working-context budget meter. When material exceeds the budget, generation falls back
/// to AI-condensed versions of the documents.
/// </summary>
public static class TokenBudget
{
    public const int DefaultBudget = 100_000;

    /// <summary>
    /// The document's token count, counting it on the spot if the stored one is missing — which
    /// only happens between the migration and the startup backfill.
    /// </summary>
    public static int TokensOf(Document document)
        => document.TokenCount ?? TokenCounter.Count(document.ExtractedText);

    private static int CondensedTokensOf(Document document)
        => document.CondensedTokenCount ?? TokenCounter.Count(document.CondensedText);

    public static BudgetStatus Assess(IReadOnlyList<Document> documents, int budget = DefaultBudget)
    {
        var fullTokens = documents.Sum(TokensOf);
        if (fullTokens <= budget)
            return new BudgetStatus(fullTokens, budget, OverBudget: false, UsingCondensed: false);

        var condensedTokens = documents.Sum(d =>
            d.Kind == DocumentKind.Upload && d.CondensedText is not null
                ? CondensedTokensOf(d)
                : TokensOf(d));
        return new BudgetStatus(fullTokens, budget, OverBudget: true,
            UsingCondensed: documents.Any(d => d.Kind == DocumentKind.Upload && d.CondensedText is not null)
                            && condensedTokens <= budget);
    }
}
