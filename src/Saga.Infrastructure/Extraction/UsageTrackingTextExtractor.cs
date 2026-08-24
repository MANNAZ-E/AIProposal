using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Extraction;

/// <summary>
/// Records one <see cref="AiUsageRecord"/> per document analysed, so Content Understanding spend
/// shows up next to LLM spend. Wraps only the billed extractor — <see cref="PlainTextExtractor"/>
/// costs nothing and stays unwrapped.
/// <para>
/// <paramref name="analyzerId"/> is recorded as the row's <c>Model</c> and nothing more. It is
/// deliberately <em>not</em> a pricing key: Content Understanding charges by the meter the work
/// landed on, and the same analyzer bills Minimal on a .docx and Standard on a PDF.
/// </para>
/// </summary>
public class UsageTrackingTextExtractor(
    IDocumentTextExtractor inner,
    string analyzerId,
    IDbContextFactory<SagaDbContext> dbFactory,
    PricingService pricing,
    ILogger<UsageTrackingTextExtractor>? logger = null) : IDocumentTextExtractor
{
    public IReadOnlySet<string> SupportedExtensions => inner.SupportedExtensions;

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        if (context is null)
            return await inner.ExtractAsync(content, fileName, context, ct);

        var record = new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            OperationId = context.OperationId,
            ProposalId = context.ProposalId,
            Service = AiServiceKind.ContentUnderstanding,
            Model = analyzerId,
            Operation = context.Operation,
            Label = Truncate(context.Label ?? Path.GetFileName(fileName), 256),
            StartedById = context.UserId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
            // The request body is the binary document, already kept in blob storage; what is
            // worth recording is which file was sent and how big it was.
            RequestText = pricing.CapturePayloads
                ? $"{Path.GetFileName(fileName)} ({Bytes(content)})"
                : null,
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await inner.ExtractAsync(content, fileName, context, ct);
            record.MinimalPages = result.Usage?.MinimalPages;
            record.BasicPages = result.Usage?.BasicPages;
            record.StandardPages = result.Usage?.StandardPages;
            record.ContextualizationTokens = result.Usage?.ContextualizationTokens;
            record.EstimatedCostUsd = pricing.EstimateExtractionUsd(result.Usage);
            record.Outcome = GenerationOutcome.Succeeded;
            if (pricing.CapturePayloads)
                record.ResponseText = result.Text;
            return result;
        }
        catch (OperationCanceledException)
        {
            record.Outcome = GenerationOutcome.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            record.Outcome = GenerationOutcome.Failed;
            record.ErrorMessage = Truncate(ex.Message, 1024);
            throw;
        }
        finally
        {
            record.Duration = stopwatch.Elapsed;
            WarnIfUsageMissing(record);
            await SaveAsync(record);
        }
    }

    /// <summary>
    /// A succeeded extraction with no reported quantities means the service sent no usage data, not
    /// that the analysis was free — and it lands as a plausible-looking 0.00 row, which is exactly
    /// how every Office upload came to look free while it was being billed. Say so in the log rather
    /// than guessing a page count: the meter follows the work performed, so nothing on this side can
    /// reconstruct it. Mirrors <c>UsageTrackingAiService.WarnIfUsageMissing</c>.
    /// </summary>
    private void WarnIfUsageMissing(AiUsageRecord record)
    {
        if (record.Outcome == GenerationOutcome.Succeeded && record.Pages is null)
        {
            logger?.LogWarning(
                "Analyzer '{Model}' reported no billed pages for operation {OperationId}; the call is "
                + "recorded at zero cost. The quantities are missing, not zero.",
                record.Model, record.OperationId);
        }
    }

    private static string Bytes(Stream content)
    {
        try { return $"{content.Length:N0} bytes"; }
        catch (NotSupportedException) { return "unknown size"; }
    }

    private static string? Truncate(string? value, int max)
        => value is not null && value.Length > max ? value[..max] : value;

    /// <summary>Never lets a metering failure break the upload it was measuring.</summary>
    private async Task SaveAsync(AiUsageRecord record)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.AiUsage.Add(record);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to record extraction usage for operation {OperationId}.", record.OperationId);
        }
    }
}
