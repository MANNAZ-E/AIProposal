using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Extraction;

/// <summary>
/// Extracts text and page positions with Azure Document Intelligence (prebuilt-read).
/// Handles PDFs (including scanned), Office documents, and images; page spans feed
/// requirement source references later in the pipeline.
/// </summary>
public class DocumentIntelligenceExtractor : IDocumentTextExtractor
{
    private readonly DocumentIntelligenceClient _client;

    public DocumentIntelligenceExtractor(IConfiguration configuration)
    {
        var endpoint = configuration["DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("DocumentIntelligence:Endpoint is not configured.");
        // Managed identity on Azure; az login / Visual Studio credential in dev. A key can be
        // supplied for local development where AAD role assignment is not set up yet.
        var key = configuration["DocumentIntelligence:Key"];
        _client = string.IsNullOrEmpty(key)
            ? new DocumentIntelligenceClient(new Uri(endpoint), new DefaultAzureCredential())
            : new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".png", ".jpg", ".jpeg", ".tiff", ".bmp", ".heif",
    };

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed, "prebuilt-read", BinaryData.FromBytes(ms.ToArray()), cancellationToken: ct);
        var result = operation.Value;

        var pages = new List<PageSpan>();
        foreach (var page in result.Pages)
        {
            // Spans are offsets into result.Content, which we store verbatim.
            foreach (var span in page.Spans)
                pages.Add(new PageSpan(page.PageNumber, span.Offset, span.Length));
        }
        return new ExtractionResult(result.Content ?? "", pages);
    }
}
