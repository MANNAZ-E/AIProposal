using System.Text;
using System.Text.RegularExpressions;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Extraction;

/// <summary>
/// Puts recovered figure text back where the figure stood. Content Understanding leaves a
/// <c>![](figures/1.1)</c> placeholder for every embedded image; substituting there — rather than
/// appending a section at the end — keeps a question next to the heading that introduces it, and
/// keeps the page map usable, which is what <see cref="Saga.Core.Pipeline.DocumentChunker"/> and the
/// "page 3" source references on every extracted requirement are built on.
/// </summary>
public static partial class FigureSplicer
{
    [GeneratedRegex(@"!\[[^\]]*\]\(figures/(?<id>[0-9.]+)\)")]
    private static partial Regex PlaceholderRegex();

    public static int CountPlaceholders(string markdown) => PlaceholderRegex().Count(markdown);

    /// <summary>
    /// Replaces the n-th placeholder with <paramref name="figureTexts"/>[n]. A null or blank entry
    /// leaves that placeholder alone — an image that was skipped, failed to read, or held nothing.
    /// </summary>
    /// <remarks>
    /// If the counts disagree the placeholders are left untouched and the recovered text is appended
    /// instead: attributing a mandatory requirement to the wrong page is worse than losing its
    /// position. The caller is expected to log that.
    /// </remarks>
    public static ExtractionResult Splice(ExtractionResult source, IReadOnlyList<string?> figureTexts)
    {
        var matches = PlaceholderRegex().Matches(source.Text);
        if (matches.Count != figureTexts.Count)
            return Append(source, figureTexts);

        var text = new StringBuilder();
        // (offset in the ORIGINAL text, change in length) — collected while building, applied to the
        // spans afterwards, so every offset below stays in one coordinate system.
        var shifts = new List<(int Offset, int Delta)>();
        var position = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(figureTexts[i])) continue;

            var match = matches[i];
            text.Append(source.Text, position, match.Index - position);
            var replacement = Render(match.Groups["id"].Value, figureTexts[i]!);
            text.Append(replacement);
            shifts.Add((match.Index, replacement.Length - match.Length));
            position = match.Index + match.Length;
        }
        if (shifts.Count == 0) return source;

        text.Append(source.Text, position, source.Text.Length - position);
        return source with { Text = text.ToString(), Pages = Shift(source.Pages, shifts) };
    }

    /// <summary>
    /// The HTML comment marks the text as read off a picture rather than typed by the client — the
    /// same shape Content Understanding uses for its own <c>&lt;!-- PageHeader: … --&gt;</c> notes.
    /// </summary>
    private static string Render(string figureId, string figureText)
        => $"<!-- figure {figureId} (embedded image) -->\n\n{figureText.Trim()}";

    /// <summary>
    /// Fallback when placeholders and images do not line up. The text still has to land inside a
    /// page span or the chunker would never read it, so it is attributed to the last page.
    /// </summary>
    private static ExtractionResult Append(ExtractionResult source, IReadOnlyList<string?> figureTexts)
    {
        var recovered = figureTexts
            .Select((t, i) => (Index: i + 1, Text: t))
            .Where(f => !string.IsNullOrWhiteSpace(f.Text))
            .ToList();
        if (recovered.Count == 0) return source;

        var text = new StringBuilder(source.Text);
        text.Append("\n\n## Text recovered from embedded images\n");
        foreach (var (index, figureText) in recovered)
            text.Append($"\n<!-- embedded image {index} -->\n\n{figureText!.Trim()}\n");

        var pages = source.Pages.ToList();
        if (pages.Count > 0)
        {
            var last = pages.Select((p, i) => (Page: p, Index: i)).MaxBy(p => p.Page.Offset);
            pages[last.Index] = last.Page with { Length = text.Length - last.Page.Offset };
        }
        return source with { Text = text.ToString(), Pages = pages };
    }

    /// <summary>
    /// Moves the page map into the spliced text's coordinates: a span that contains a substitution
    /// grows by its delta, a span that starts after one moves by it. A substitution that falls
    /// outside every span is charged to the nearest span starting before it, so the recovered text
    /// stays inside the page map and still reaches the chunker.
    /// </summary>
    private static List<PageSpan> Shift(IReadOnlyList<PageSpan> pages, List<(int Offset, int Delta)> shifts)
    {
        if (pages.Count == 0) return [];
        var byOffset = Enumerable.Range(0, pages.Count).OrderBy(i => pages[i].Offset).ToList();
        var owners = shifts.Select(s => OwnerOf(pages, byOffset, s.Offset)).ToList();

        return pages.Select((page, index) =>
        {
            var before = 0;
            var inside = 0;
            for (var i = 0; i < shifts.Count; i++)
            {
                if (owners[i] == index) inside += shifts[i].Delta;
                else if (shifts[i].Offset < page.Offset) before += shifts[i].Delta;
            }
            return page with { Offset = page.Offset + before, Length = page.Length + inside };
        }).ToList();
    }

    private static int OwnerOf(IReadOnlyList<PageSpan> pages, List<int> byOffset, int offset)
    {
        for (var i = byOffset.Count - 1; i >= 0; i--)
        {
            if (pages[byOffset[i]].Offset <= offset) return byOffset[i];
        }
        return byOffset[0];
    }
}
