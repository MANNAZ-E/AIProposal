namespace Saga.Core.Pipeline;

/// <summary>
/// Names a chat from its first question. Nobody renames anything, so the derived title has to
/// be good enough on its own: one line, cut at a word boundary, no trailing punctuation.
/// </summary>
public static class ChatTitle
{
    private const int MaxLength = 60;

    public static string FromQuestion(string question)
    {
        var words = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var collapsed = string.Join(' ', words);
        if (collapsed.Length == 0) return "New chat";

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
