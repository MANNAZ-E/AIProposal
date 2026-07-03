namespace Saga.Core.Abstractions;

/// <summary>One page (or section) of extracted text, as offsets into the full text.</summary>
public record PageSpan(int Page, int Offset, int Length);

public record ExtractionResult(string Text, IReadOnlyList<PageSpan> Pages);

/// <summary>Extracts plain text (with page positions) from an uploaded client document.</summary>
public interface IDocumentTextExtractor
{
    /// <summary>File extensions this extractor accepts, lowercase with dot (e.g. ".pdf").</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    Task<ExtractionResult> ExtractAsync(Stream content, string fileName, CancellationToken ct = default);
}
