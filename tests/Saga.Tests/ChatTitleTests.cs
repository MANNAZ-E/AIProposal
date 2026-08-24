using Saga.Core.Pipeline;

namespace Saga.Tests;

public class ChatTitleTests
{
    [Fact]
    public void Short_questions_are_kept_as_they_are()
        => Assert.Equal("What is the deadline", ChatTitle.FromQuestion("What is the deadline?"));

    [Fact]
    public void Whitespace_and_newlines_collapse_to_one_line()
        => Assert.Equal("Two lines here", ChatTitle.FromQuestion("Two   lines\r\n here"));

    [Fact]
    public void Long_questions_are_cut_at_a_word_boundary()
    {
        const string question =
            "Which of the requirements in the tender are we unable to meet with the current scope?";
        var title = ChatTitle.FromQuestion(question);

        Assert.True(title.Length <= 60);
        Assert.True(title.Length > 40); // It uses the space it has.
        // Cut between words, never mid-word: what follows the cut is a space.
        Assert.StartsWith(title + " ", question);
    }

    [Fact]
    public void A_single_very_long_word_is_cut_mid_word()
    {
        var title = ChatTitle.FromQuestion(new string('x', 300));
        Assert.Equal(60, title.Length);
    }

    [Fact]
    public void A_blank_question_falls_back_to_a_name()
        => Assert.Equal("New chat", ChatTitle.FromQuestion("   "));
}
