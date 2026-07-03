using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public record ExtractionProgress(string DocumentName, int Chunk, int TotalChunks);

/// <summary>
/// Extracts the requirements and criteria list (spec §12) by running the light model over
/// each document chunk-by-chunk (aligned to pages so sources stay accurate), then merging.
/// </summary>
public class RequirementsExtractionService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai)
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<(Guid RunId, RequirementsPayload Payload)> ExtractAsync(Guid proposalId, Guid userId,
        Func<ExtractionProgress, Task>? onProgress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var existing = await db.Artifacts
            .FirstOrDefaultAsync(a => a.ProposalId == proposalId && a.Type == ArtifactType.Requirements, ct);
        if (existing?.IsLocked == true)
            throw new InvalidOperationException("The requirements list is locked. Unlock it before regenerating.");

        var documents = await db.Documents.Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        if (!documents.Any(d => !string.IsNullOrWhiteSpace(d.ExtractedText)))
            throw new InvalidOperationException("Upload client documents or add notes before extracting requirements.");

        var run = new GenerationRun
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            ArtifactType = ArtifactType.Requirements,
            Model = "",
            StartedById = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
        };
        var stopwatch = Stopwatch.StartNew();
        var payload = new RequirementsPayload();

        try
        {
            foreach (var document in documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)))
            {
                var pageMap = ParsePageMap(document.PageMapJson);
                var chunks = DocumentChunker.Chunk(document.ExtractedText, pageMap);
                for (var i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (onProgress is not null)
                        await onProgress(new ExtractionProgress(document.Name, i + 1, chunks.Count));

                    var chunk = chunks[i];
                    var request = new AiRequest(
                        RequirementsPrompts.BuildSystemPrompt(document.Name, chunk.LocationLabel),
                        [AiMessage.User(chunk.Text)],
                        AiModelTier.Light);
                    var completion = await ai.CompleteAsync(request, ct);

                    run.PromptTokens += completion.PromptTokens;
                    run.CompletionTokens += completion.CompletionTokens;
                    run.Model = completion.Model;

                    foreach (var item in ParseItems(completion.Text))
                    {
                        item.SourceDocument = document.Name;
                        item.SourceLocation = chunk.LocationLabel;
                        payload.Items.Add(item);
                    }
                }
            }

            Deduplicate(payload);
            run.Outcome = GenerationOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            run.Outcome = GenerationOutcome.Cancelled;
            throw;
        }
        finally
        {
            run.Duration = stopwatch.Elapsed;
            db.GenerationRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return (run.Id, payload);
    }

    private static List<PageSpan>? ParsePageMap(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<PageSpan>>(json, ParseOptions);

    /// <summary>Parses the model's JSON array, tolerating markdown fences and stray prose.</summary>
    internal static List<RequirementItem> ParseItems(string modelOutput)
        => Ai.ModelJson.ParseArray<RequirementItem>(modelOutput)
            .Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();

    /// <summary>Removes near-duplicate requirements extracted from overlapping wording.</summary>
    internal static void Deduplicate(RequirementsPayload payload)
    {
        var seen = new HashSet<string>();
        payload.Items = payload.Items
            .Where(item => seen.Add(Normalize(item.Text)))
            .ToList();

        static string Normalize(string text)
            => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
    }
}
