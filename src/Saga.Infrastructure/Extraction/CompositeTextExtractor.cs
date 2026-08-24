using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Extraction;

/// <summary>Routes a file to the first registered extractor that supports its extension.</summary>
public class CompositeTextExtractor(IEnumerable<IDocumentTextExtractor> extractors) : IDocumentTextExtractor
{
    private readonly List<IDocumentTextExtractor> _extractors = extractors.ToList();

    public IReadOnlySet<string> SupportedExtensions
        => _extractors.SelectMany(e => e.SupportedExtensions).ToHashSet();

    public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var extractor = _extractors.FirstOrDefault(e => e.SupportedExtensions.Contains(extension))
            ?? throw new InvalidOperationException(
                $"Files of type '{extension}' are not supported. Supported types: {string.Join(", ", SupportedExtensions.Order())}.");
        return extractor.ExtractAsync(content, fileName, context, ct);
    }
}
