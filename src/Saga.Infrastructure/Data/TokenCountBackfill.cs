using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Saga.Core.Tokenization;

namespace Saga.Infrastructure.Data;

/// <summary>
/// Fills the token counts on rows written before the columns existed. Tokenising cannot happen
/// in SQL, so it cannot be part of the migration; this runs once at startup instead and costs
/// three empty queries on every later boot.
/// </summary>
public class TokenCountBackfill(
    IDbContextFactory<SagaDbContext> dbFactory,
    ILogger<TokenCountBackfill> logger)
{
    // Extracted text runs to megabytes per row, so the work is batched rather than loaded whole.
    private const int BatchSize = 50;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var documents = await BackfillAsync(
            db => db.Documents.Where(d => d.TokenCount == null),
            d => d.TokenCount = TokenCounter.Count(d.ExtractedText), ct);

        // Only where there is condensed text: a null count elsewhere means "nothing condensed".
        var condensed = await BackfillAsync(
            db => db.Documents.Where(d => d.CondensedText != null && d.CondensedTokenCount == null),
            d => d.CondensedTokenCount = TokenCounter.Count(d.CondensedText), ct);

        var versions = await BackfillAsync(
            db => db.DocumentVersions.Where(v => v.TokenCount == null),
            v => v.TokenCount = TokenCounter.Count(v.Text), ct);

        if (documents + condensed + versions > 0)
            logger.LogInformation(
                "Backfilled token counts: {Documents} documents, {Condensed} condensed texts, {Versions} versions.",
                documents, condensed, versions);
    }

    /// <param name="fill">
    /// Must set the column the query filters on, or the batch comes back forever.
    /// </param>
    private async Task<int> BackfillAsync<T>(
        Func<SagaDbContext, IQueryable<T>> pending, Action<T> fill, CancellationToken ct)
        where T : class
    {
        var total = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var batch = await pending(db).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
                return total;

            foreach (var row in batch)
                fill(row);
            await db.SaveChangesAsync(ct);
            total += batch.Count;
        }
    }
}
