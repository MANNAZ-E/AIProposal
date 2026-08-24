using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ContentUnderstandingExtractor>? logger;

    public ContentUnderstandingExtractor(IConfiguration configuration,
        ILogger<ContentUnderstandingExtractor>? logger = null)
    {
        this.logger = logger;

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

        // A single file normally yields one document content; loop anyway so nothing is dropped
        // silently, shifting each part's spans by where its Markdown lands in the combined text.
        foreach (var document in operation.Value.Contents.OfType<DocumentContent>())
        {
            if (markdown.Length > 0)
                markdown.Append("\n\n");
            var baseOffset = markdown.Length;

            // Page geometry, for the page map only. It is not the billing unit — Office files
            // carry none at all, so counting it here is what metered them as free.
            foreach (var page in document.Pages)
                foreach (var span in page.Spans)
                    pages.Add(new PageSpan(page.PageNumber, baseOffset + span.Offset, span.Length));

            markdown.Append(document.Markdown);
        }

        if (markdown.Length == 0)
            throw new InvalidOperationException(
                $"Content Understanding returned no document content for '{Path.GetFileName(fileName)}'.");

        return new ExtractionResult(markdown.ToString(), pages, BilledUsage(operation, fileName));
    }

    /// <summary>
    /// The quantities Azure says it billed, read off the <c>usage</c> field the analyze operation
    /// returns beside its result. This is the only honest source: the meter charged follows the work
    /// performed rather than the analyzer requested, so nothing on our side can derive it. An absent
    /// counter inside a present <c>usage</c> genuinely means zero of that meter; an absent
    /// <c>usage</c> means we were told nothing, and null carries that all the way to the row.
    /// </summary>
    private ExtractionUsage? BilledUsage(Operation<AnalysisResult> operation, string fileName)
    {
        var usage = operation.GetUsage();
        if (usage is null)
        {
            logger?.LogWarning(
                "Content Understanding reported no usage for '{File}'; the call is recorded without "
                + "a billed quantity. It was still charged — the numbers are missing, not zero.",
                Path.GetFileName(fileName));
            return null;
        }

        var billed = new ExtractionUsage(
            usage.DocumentPagesMinimal ?? 0,
            usage.DocumentPagesBasic ?? 0,
            usage.DocumentPagesStandard ?? 0,
            usage.ContextualizationTokens ?? 0);

        // The only view into which meter a given upload actually hit, which is otherwise invisible.
        logger?.LogDebug("'{File}' billed {Minimal} minimal, {Basic} basic, {Standard} standard "
            + "pages and {Tokens} contextualization tokens.", Path.GetFileName(fileName),
            billed.MinimalPages, billed.BasicPages, billed.StandardPages, billed.ContextualizationTokens);

        return billed;
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
