using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using System.Text;

namespace Saga.Infrastructure.Extraction;

/// <summary>
/// Extracts Markdown and page positions with Azure Content Understanding's prebuilt Layout
/// analyzer. Handles PDFs (including scanned), Office documents, and images; the Markdown keeps
/// headings, lists and tables intact for the prompts, and page spans feed requirement source
/// references later in the pipeline.
/// </summary>
public class ContentUnderstandingExtractor : IDocumentTextExtractor
{
    /// <summary>
    /// Layout is pure content extraction — no language or embedding model deployment is needed
    /// on the Foundry resource, unlike the RAG analyzers (prebuilt-documentSearch and friends).
    /// </summary>
    public const string AnalyzerId = "prebuilt-layout";

    private readonly ContentUnderstandingClient _client;

    public ContentUnderstandingExtractor(IConfiguration configuration)
    {
        var endpoint = configuration["ContentUnderstanding:Endpoint"]
            ?? throw new InvalidOperationException("ContentUnderstanding:Endpoint is not configured.");

        var options = new ContentUnderstandingClientOptions();
        options.AddPolicy(new Utf16SpansPolicy(), HttpPipelinePosition.PerCall);

        // Managed identity on Azure; az login / Visual Studio credential in dev. Both need
        // "Cognitive Services User" on the Foundry resource — owner alone is not enough.
        _client = new ContentUnderstandingClient(new Uri(endpoint), new DefaultAzureCredential(), options);
    }

    /// <summary>Shared with <see cref="FakeDocumentExtractor"/> so the offline stand-in accepts the same types.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".png", ".jpg", ".jpeg", ".tiff", ".bmp", ".heif",
    };

    public IReadOnlySet<string> SupportedExtensions => Extensions;

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);

        var operation = await _client.AnalyzeBinaryAsync(
            WaitUntil.Completed,
            AnalyzerId,
            BinaryData.FromBytes(ms.ToArray()),
            // Client material stays in the resource's geography rather than the "global" default.
            processingLocation: ProcessingLocation.Geography,
            cancellationToken: ct);

        var markdown = new StringBuilder();
        var pages = new List<PageSpan>();
        var pageCount = 0;

        // A single file normally yields one document content; loop anyway so nothing is dropped
        // silently, shifting each part's spans by where its Markdown lands in the combined text.
        foreach (var document in operation.Value.Contents.OfType<DocumentContent>())
        {
            if (markdown.Length > 0)
                markdown.Append("\n\n");
            var baseOffset = markdown.Length;

            // Pages are what Content Understanding bills by, so the count is recorded too.
            pageCount += document.Pages.Count;
            foreach (var page in document.Pages)
                foreach (var span in page.Spans)
                    pages.Add(new PageSpan(page.PageNumber, baseOffset + span.Offset, span.Length));

            markdown.Append(document.Markdown);
        }

        if (markdown.Length == 0)
            throw new InvalidOperationException(
                $"Content Understanding returned no document content for '{Path.GetFileName(fileName)}'.");

        return new ExtractionResult(markdown.ToString(), pages, pageCount);
    }

    /// <summary>
    /// The service reports span offsets in Unicode code points by default, while the offsets we
    /// store index a .NET string (UTF-16 code units) — so page attribution would drift past the
    /// first emoji or other non-BMP character. The convenience overload has no stringEncoding
    /// parameter, so ask for utf16 on the wire instead.
    /// </summary>
    private sealed class Utf16SpansPolicy : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Uri.Path.Contains(":analyze", StringComparison.Ordinal))
                message.Request.Uri.AppendQuery("stringEncoding", "utf16");
        }
    }
}
