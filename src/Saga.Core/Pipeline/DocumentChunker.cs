using Saga.Core.Abstractions;

namespace Saga.Core.Pipeline;

public record DocumentChunk(string Text, string LocationLabel);

/// <summary>
/// Splits an extracted document into chunks for per-chunk requirements extraction, aligned to
/// page boundaries so source references stay accurate. Chunk size is a tuning knob.
/// </summary>
public static class DocumentChunker
{
    public const int DefaultMaxChars = 24_000;

    public static IReadOnlyList<DocumentChunk> Chunk(string text, IReadOnlyList<PageSpan>? pages,
        int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        if (pages is null || pages.Count == 0)
            return ChunkByLength(text, maxChars);

        var chunks = new List<DocumentChunk>();
        var currentPages = new List<PageSpan>();
        var currentLength = 0;

        foreach (var page in pages.OrderBy(p => p.Offset))
        {
            if (currentLength > 0 && currentLength + page.Length > maxChars)
            {
                chunks.Add(BuildChunk(text, currentPages));
                currentPages = [];
                currentLength = 0;
            }
            currentPages.Add(page);
            currentLength += page.Length;
        }
        if (currentPages.Count > 0)
            chunks.Add(BuildChunk(text, currentPages));
        return chunks;
    }

    private static DocumentChunk BuildChunk(string text, List<PageSpan> pages)
    {
        var start = Math.Min(pages[0].Offset, text.Length);
        var end = Math.Min(pages[^1].Offset + pages[^1].Length, text.Length);
        var label = pages[0].Page == pages[^1].Page
            ? $"page {pages[0].Page}"
            : $"pages {pages[0].Page}–{pages[^1].Page}";
        return new DocumentChunk(text[start..end], label);
    }

    private static List<DocumentChunk> ChunkByLength(string text, int maxChars)
    {
        var chunks = new List<DocumentChunk>();
        var position = 0;
        var part = 1;
        while (position < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - position);
            // Prefer breaking at a paragraph boundary in the second half of the window.
            if (position + length < text.Length)
            {
                var slice = text.AsSpan(position, length);
                var lastBreak = slice.LastIndexOf("\n\n");
                if (lastBreak > length / 2)
                    length = lastBreak;
            }
            chunks.Add(new DocumentChunk(text.Substring(position, length), $"part {part}"));
            position += length;
            part++;
        }
        return chunks;
    }
}
