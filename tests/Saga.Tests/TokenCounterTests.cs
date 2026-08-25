using Saga.Core.Tokenization;

namespace Saga.Tests;

public class TokenCounterTests
{
    [Fact]
    public void Counts_match_the_o200k_base_encoding()
    {
        // Known o200k_base values. If the tokenizer ever resolved a different encoding these
        // would drift, which is the point: nothing else would notice a silent fallback.
        Assert.Equal(2, TokenCounter.Count("hello world"));
        Assert.Equal(1000, TokenCounter.Count(new string('x', 8000)));
    }

    [Fact]
    public void Empty_and_null_text_cost_nothing()
    {
        Assert.Equal(0, TokenCounter.Count(null));
        Assert.Equal(0, TokenCounter.Count(""));
    }

    [Fact]
    public void Danish_prose_lands_near_the_four_characters_per_token_rule_of_thumb()
    {
        // The old heuristic was chars/4. Real proposals are Danish and English prose, where
        // that happens to be close - so a count wildly off it means the wrong vocab loaded,
        // not that the estimate improved.
        var danish = "Mannaz leverer skræddersyet lederudvikling til organisationer i hele Norden. "
                   + "Vi kombinerer forskningsbaseret viden med praktisk erfaring fra konsulentarbejde.";

        var tokens = TokenCounter.Count(danish);

        Assert.InRange(tokens, danish.Length / 4 * 0.7, danish.Length / 4 * 1.3);
    }

    [Fact]
    public void Text_with_no_latin_characters_still_counts()
    {
        // Proves the embedded vocab resolved: a failed load throws rather than returning a
        // plausible-looking number, and nothing here can be served by an ASCII fast path.
        Assert.True(TokenCounter.Count("这是一个没有任何拉丁字母的句子。") > 0);
    }
}
