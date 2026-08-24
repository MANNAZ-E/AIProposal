namespace Saga.Core.Abstractions;

/// <summary>One page (or section) of extracted text, as offsets into the full text.</summary>
public record PageSpan(int Page, int Offset, int Length);

/// <param name="PageCount">
/// Pages the service reported — the unit Content Understanding bills by. 0 for extractors
/// that cost nothing (plain text) or that could not determine a count.
/// </param>
public record ExtractionResult(string Text, IReadOnlyList<PageSpan> Pages, int PageCount = 0);

/// <summary>Extracts plain text (with page positions) from an uploaded client document.</summary>
public interface IDocumentTextExtractor
{
    /// <summary>File extensions this extractor accepts, lowercase with dot (e.g. ".pdf").</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default);
}
