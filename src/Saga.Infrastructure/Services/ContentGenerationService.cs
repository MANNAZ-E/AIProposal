using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public record ContentProgress(int Unit, int TotalUnits, string Title);

/// <summary>
/// Generates the Content artifact unit by unit (slide/section) from the approved structure.
/// Locked units survive whole-content regeneration; single units regenerate with steering.
/// </summary>
public class ContentGenerationService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai,
    WorkingContextService contextService)
{
    /// <summary>
    /// Generates all units from the structure. Existing locked units are carried over
    /// untouched. Returns the staged payload — caller applies via GenerationService.ApplyAsync.
    /// </summary>
    public async Task<(Guid RunId, ContentPayload Payload)> GenerateAllAsync(Guid proposalId, Guid userId,
        string? instruction, Func<ContentProgress, Task>? onProgress = null, Func<string, Task>? onDelta = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var voice = await db.MannazVoiceSettings.FirstAsync(ct);
        var loaded = await contextService.LoadAsync(proposalId, null, ct);
        var artifacts = loaded.Artifacts;

        var contentArtifact = artifacts.FirstOrDefault(a => a.Type == ArtifactType.Content);
        if (contentArtifact?.IsLocked == true)
            throw new InvalidOperationException("The content is locked. Unlock it before regenerating.");

        var structureArtifact = artifacts.FirstOrDefault(a => a.Type == ArtifactType.Structure);
        var structure = StructurePayload.FromJson(structureArtifact?.ContentJson);
        if (structure.Items.Count == 0)
            throw new InvalidOperationException("Generate and approve the structure before generating content.");

        var existing = ContentPayload.FromJson(contentArtifact?.ContentJson);
        var context = WorkingContextBuilder.Build(WorkingContextKind.FullProject, loaded.Documents, artifacts,
            excludeArtifact: ArtifactType.Content, useCondensedDocuments: loaded.UseCondensed);

        var run = NewRun(proposalId, ArtifactType.Content, userId, instruction);
        var stopwatch = Stopwatch.StartNew();
        var payload = new ContentPayload();

        try
        {
            for (var i = 0; i < structure.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = structure.Items[i];
                if (onProgress is not null)
                    await onProgress(new ContentProgress(i + 1, structure.Items.Count, item.Title));

                // A locked unit for this structure item survives regeneration untouched.
                var locked = existing.Units.FirstOrDefault(u => u.StructureItemId == item.Id && u.IsLocked);
                if (locked is not null)
                {
                    payload.Units.Add(locked);
                    continue;
                }

                var body = await GenerateUnitBodyAsync(proposal, voice, item, i + 1, structure.Items.Count,
                    context, instruction, run, onDelta, ct);
                payload.Units.Add(new ContentUnit
                {
                    StructureItemId = item.Id,
                    Title = item.Title,
                    KeyMessage = item.KeyMessage,
                    BodyMarkdown = body,
                });
            }
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

    /// <summary>Regenerates one unit; returns the staged body for diff review.</summary>
    public async Task<(Guid RunId, string Body)> RegenerateUnitAsync(Guid proposalId, Guid unitId, Guid userId,
        string? instruction, Func<string, Task>? onDelta = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var voice = await db.MannazVoiceSettings.FirstAsync(ct);
        var loaded = await contextService.LoadAsync(proposalId, null, ct);
        var artifacts = loaded.Artifacts;

        var contentArtifact = artifacts.FirstOrDefault(a => a.Type == ArtifactType.Content)
            ?? throw new InvalidOperationException("There is no content yet.");
        var content = ContentPayload.FromJson(contentArtifact.ContentJson);
        var unit = content.Units.FirstOrDefault(u => u.Id == unitId)
            ?? throw new InvalidOperationException("The unit no longer exists.");
        if (unit.IsLocked || contentArtifact.IsLocked)
            throw new InvalidOperationException("This unit is locked. Unlock it before regenerating.");

        var structure = StructurePayload.FromJson(
            artifacts.FirstOrDefault(a => a.Type == ArtifactType.Structure)?.ContentJson);
        var item = structure.Items.FirstOrDefault(s => s.Id == unit.StructureItemId)
            ?? new StructureItem { Title = unit.Title, KeyMessage = unit.KeyMessage };
        var position = Math.Max(1, structure.Items.FindIndex(s => s.Id == unit.StructureItemId) + 1);
        var total = Math.Max(structure.Items.Count, 1);

        var context = WorkingContextBuilder.Build(WorkingContextKind.FullProject, loaded.Documents, artifacts,
            useCondensedDocuments: loaded.UseCondensed);

        var run = NewRun(proposalId, ArtifactType.Content, userId, instruction);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var body = await GenerateUnitBodyAsync(proposal, voice, item, position, total,
                context, instruction, run, onDelta, ct);
            run.Outcome = GenerationOutcome.Succeeded;
            return (run.Id, body);
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
    }

    private async Task<string> GenerateUnitBodyAsync(Proposal proposal, MannazVoiceSettings voice,
        StructureItem item, int position, int total, string context, string? instruction,
        GenerationRun run, Func<string, Task>? onDelta, CancellationToken ct)
    {
        var prompt = ArtifactPrompts.BuildContentUnitPrompt(proposal, voice, item, position, total, instruction);
        var text = new System.Text.StringBuilder();
        await foreach (var evt in ai.StreamAsync(new AiRequest(prompt, [AiMessage.User(context)]), ct))
        {
            switch (evt)
            {
                case AiStreamEvent.Delta d:
                    text.Append(d.Text);
                    if (onDelta is not null) await onDelta(d.Text);
                    break;
                case AiStreamEvent.Completed c:
                    run.PromptTokens += c.PromptTokens;
                    run.CompletionTokens += c.CompletionTokens;
                    run.Model = c.Model;
                    break;
            }
        }
        return text.ToString().Trim();
    }

    private static GenerationRun NewRun(Guid proposalId, ArtifactType type, Guid userId, string? instruction)
        => new()
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            ArtifactType = type,
            Model = "",
            InstructionText = string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim(),
            StartedById = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
        };
}
