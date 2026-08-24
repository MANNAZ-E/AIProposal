using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Extraction;

/// <summary>Reads .txt/.md files directly — no external service needed.</summary>
public class PlainTextExtractor : IDocumentTextExtractor
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".txt", ".md" };

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(ct);
        // Explicitly free rather than unknown: reading a local file costs nothing, and this
        // extractor is registered outside the meter, so no usage row is written for it at all.
        return new ExtractionResult(text, [new PageSpan(1, 0, text.Length)], ExtractionUsage.Free);
    }
}
