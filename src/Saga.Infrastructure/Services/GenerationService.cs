using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public record GenerationResult(Guid OperationId, string Text, int PromptTokens, int CompletionTokens, string Model);

public class GenerationService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai,
    IWebResearchService webResearch,
    WorkingContextService contextService,
    AiUsageService usage)
{
    /// <summary>
    /// Generates content for an artifact, streaming deltas via <paramref name="onDelta"/>.
    /// The result is NOT applied to the artifact — the caller shows a diff and calls
    /// <see cref="ApplyAsync"/> to accept or <see cref="MarkRejectedAsync"/> to discard.
    /// The call is logged to AiUsage by the usage decorator — see <see cref="AiCallContext"/>.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(Guid proposalId, ArtifactType type, Guid userId,
        string? instruction, Func<string, Task>? onDelta, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var existing = await db.Artifacts.FirstOrDefaultAsync(a => a.ProposalId == proposalId && a.Type == type, ct);
        if (existing?.IsLocked == true)
            throw new InvalidOperationException("The artifact is locked. Unlock it before regenerating.");

        var voice = await db.MannazVoiceSettings.FirstAsync(ct);
        var loaded = await contextService.LoadAsync(proposalId, userId, null, ct);

        if (!loaded.Documents.Any(d => !string.IsNullOrWhiteSpace(d.ExtractedText)))
            throw new InvalidOperationException("Upload client documents or add notes before generating.");

        var systemPrompt = ArtifactPrompts.BuildSystemPrompt(type, proposal, voice, instruction);
        var context = WorkingContextBuilder.Build(
            WorkingContextBuilder.ContextFor(type), loaded.Documents, loaded.Artifacts,
            excludeArtifact: type, useCondensedDocuments: loaded.UseCondensed);

        // Client profile: add live web research (Bing grounding via Foundry) when configured.
        if (type == ArtifactType.ClientProfile)
        {
            var searchName = string.IsNullOrWhiteSpace(proposal.ResearchClientName)
                ? proposal.ClientName
                : proposal.ResearchClientName;
            var findings = await webResearch.ResearchClientAsync(
                searchName, proposal.ClientWebsite, proposal.Description, ct);
            if (!string.IsNullOrWhiteSpace(findings))
                context += $"\n<web_research>\nLive web research about the client, with sources. Ground the profile in this.\n{findings}\n</web_research>\n";
        }

        var operationId = Guid.NewGuid();
        var request = new AiRequest(systemPrompt, [AiMessage.User(context)], TierFor(type),
            new AiCallContext(operationId, AiOperation.GenerateArtifact, proposalId, userId,
                ArtifactType: type,
                InstructionText: string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim()));

        var text = new System.Text.StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var model = "";
        await foreach (var evt in ai.StreamAsync(request, ct))
        {
            switch (evt)
            {
                case AiStreamEvent.Delta d:
                    text.Append(d.Text);
                    if (onDelta is not null) await onDelta(d.Text);
                    break;
                case AiStreamEvent.Completed c:
                    (promptTokens, completionTokens, model) = (c.PromptTokens, c.CompletionTokens, c.Model);
                    break;
            }
        }

        return new GenerationResult(operationId, text.ToString().Trim(), promptTokens, completionTokens, model);
    }

    /// <summary>Accepts a generation: snapshots the previous version, then replaces the content.</summary>
    public async Task<Artifact> ApplyAsync(Guid proposalId, ArtifactType type, Guid userId,
        string? contentMarkdown, string? contentJson, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        var artifact = await db.Artifacts.FirstOrDefaultAsync(a => a.ProposalId == proposalId && a.Type == type, ct);
        if (artifact is null)
        {
            artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Type = type,
            };
            db.Artifacts.Add(artifact);
        }
        else if (artifact.IsLocked)
        {
            throw new InvalidOperationException("The artifact is locked.");
        }

        artifact.ContentMarkdown = contentMarkdown;
        artifact.ContentJson = contentJson;
        artifact.Status = ArtifactStatus.Generated;
        artifact.GeneratedAt = now;
        artifact.UpdatedAt = now;

        db.ArtifactVersions.Add(ArtifactService.Snapshot(artifact, VersionOrigin.Generated, userId, now));
        await ArtifactService.TouchProposalAsync(db, proposalId, now, ct);
        await db.SaveChangesAsync(ct);
        return artifact;
    }

    /// <summary>The user rejected the generated result in the diff review.</summary>
    public Task MarkRejectedAsync(Guid operationId, CancellationToken ct = default)
        => usage.MarkOperationRejectedAsync(operationId, ct);

    private static AiModelTier TierFor(ArtifactType type)
        => type == ArtifactType.Requirements ? AiModelTier.Light : AiModelTier.Strong;
}
