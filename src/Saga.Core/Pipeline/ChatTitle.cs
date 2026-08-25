namespace Saga.Core.Pipeline;

/// <summary>
/// Names a chat, or a bid team thread, from its first message. A title can be changed
/// afterwards but almost never is, so the derived one has to be good enough on its own: one
/// line, cut at a word boundary, no trailing punctuation.
/// </summary>
public static class ChatTitle
{
    private const int MaxLength = 60;

    /// <param name="fallback">What an empty or whitespace-only message is called instead.</param>
    public static string FromQuestion(string question, string fallback = "New chat")
    {
        var words = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var collapsed = string.Join(' ', words);
        if (collapsed.Length == 0) return fallback;

        if (collapsed.Length > MaxLength)
        {
            var cut = collapsed[..MaxLength];
            var lastSpace = cut.LastIndexOf(' ');
            // A single very long word has no space to cut at, so it is cut mid-word instead.
            collapsed = lastSpace > 0 ? cut[..lastSpace] : cut;
        }

        return collapsed.TrimEnd('.', ',', ':', ';', '!', '?', '-', '–', '—', ' ') is { Length: > 0 } trimmed
            ? trimmed
            : collapsed;
    }
}
