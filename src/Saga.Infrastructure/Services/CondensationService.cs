using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Produces AI-condensed versions of uploaded documents so oversized material still fits
/// the context budget. Requirements extraction always uses the full text (it runs chunked);
/// condensation only affects prose generation and chat.
/// </summary>
public class CondensationService(IDbContextFactory<SagaDbContext> dbFactory, IAiService ai)
{
    private const string SystemPrompt = """
        Condense the following client/tender document for use as AI context. Keep, in the document's own language:
        - every requirement, criterion, deadline and practical instruction (verbatim where possible)
        - the client's goals, challenges and key facts
        - names, numbers, dates and defined terms
        Remove boilerplate, repetition and formalities. Target roughly a quarter of the original length.
        Return only the condensed text as markdown.
        """;

    /// <summary>Condenses uploads that don't have a current condensed version yet.</summary>
    public async Task EnsureCondensedAsync(Guid proposalId,
        Func<string, Task>? onProgress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var documents = await db.Documents
            .Where(d => d.ProposalId == proposalId && d.Kind == DocumentKind.Upload
                        && d.CondensedText == null && d.ExtractedText != "")
            .ToListAsync(ct);

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (onProgress is not null)
                await onProgress(document.Name);

            // Condense chunk by chunk so very large documents fit the model too.
            var chunks = DocumentChunker.Chunk(document.ExtractedText, null);
            var parts = new List<string>();
            foreach (var chunk in chunks)
            {
                var completion = await ai.CompleteAsync(
                    new AiRequest(SystemPrompt, [AiMessage.User(chunk.Text)], AiModelTier.Light), ct);
                parts.Add(completion.Text.Trim());
            }
            document.CondensedText = string.Join("\n\n", parts);
        }
        await db.SaveChangesAsync(ct);
    }
}
