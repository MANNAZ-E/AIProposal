using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Extraction;

/// <summary>
/// Stand-in used when ContentUnderstanding:Endpoint is not configured, so every file type
/// Azure Content Understanding handles can still be uploaded offline (and in dev). Produces
/// clearly-labelled placeholder text instead of a real extraction.
/// </summary>
public class FakeDocumentExtractor : IDocumentTextExtractor
{
    public IReadOnlySet<string> SupportedExtensions => ContentUnderstandingExtractor.Extensions;

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        // Drain the stream so upload behaves like the real extractor (and reports a size).
        long size = 0;
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
            size += read;

        var text = $"""
            *(Placeholder text from the offline stand-in extractor — configure ContentUnderstanding:Endpoint
            to extract the real content of "{Path.GetFileName(fileName)}" ({size:N0} bytes).)*

            ## Background and purpose
            The client is seeking a partner for a development programme. This placeholder stands in for
            the document's actual content so the rest of the pipeline can be exercised offline.

            ## Requirements for the offer
            - The offer must be submitted no later than the stated deadline.
            - The supplier must document experience with comparable assignments.
            - The solution will be evaluated on quality of the proposed approach.

            ## Practical information
            Questions can be submitted in writing. The placeholder ends here — edit this text via
            "Edit text" if you want to work with real content before Azure is wired up.
            """;
        // Reports one page so the usage decorator has a plausible billing unit to record.
        return new ExtractionResult(text, [new PageSpan(1, 0, text.Length)], PageCount: 1);
    }
}
