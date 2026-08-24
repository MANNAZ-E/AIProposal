using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Click-to-run review (spec §16): checks the proposal against the current requirements list
/// and produces a coverage report. It reports — it never changes the proposal. The caller
/// applies the result to the Review artifact via <see cref="GenerationService.ApplyAsync"/>.
/// </summary>
public class ReviewService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai,
    WorkingContextService contextService)
{
    private sealed class ReviewRow
    {
        public string? RequirementId { get; set; }
        public ReviewCoverage Coverage { get; set; }
        public string? WhereAddressed { get; set; }
        public string? Improvement { get; set; }
        public string? Risk { get; set; }
    }

    public async Task<(Guid OperationId, ReviewPayload Payload)> GenerateAsync(Guid proposalId, Guid userId,
        Func<string, Task>? onProgress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var loaded = await contextService.LoadAsync(proposalId, userId, null, ct);

        var requirementsArtifact = loaded.Artifacts.FirstOrDefault(a => a.Type == ArtifactType.Requirements);
        var requirements = RequirementsPayload.FromJson(requirementsArtifact?.ContentJson);
        if (requirements.Items.Count == 0)
            throw new InvalidOperationException("There is no requirements list to review against. Generate requirements first.");

        var contentArtifact = loaded.Artifacts.FirstOrDefault(a => a.Type == ArtifactType.Content);
        if (ContentPayload.FromJson(contentArtifact?.ContentJson).Units.Count == 0)
            throw new InvalidOperationException("There is no proposal content to review yet. Generate content first.");

        // System prompt, then material, then the instruction: the review is re-run as the draft
        // changes, so the requirements list and working context sit in front of the task and stay
        // cacheable between runs.
        var systemPrompt = ReviewPrompts.BuildSystemPrompt(proposal);
        var context = WorkingContextBuilder.Build(WorkingContextKind.FullProject,
            loaded.Documents, loaded.Artifacts,
            excludeArtifact: ArtifactType.Review, useCondensedDocuments: loaded.UseCondensed);

        var operationId = Guid.NewGuid();
        var request = new AiRequest(systemPrompt,
            [
                AiMessage.User(ReviewPrompts.BuildRequirementsMessage(requirements)),
                AiMessage.User(context),
                AiMessage.User(ReviewPrompts.Instruction),
            ],
            Context: new AiCallContext(operationId, AiOperation.ReviewDraft, proposalId, userId,
                ArtifactType: ArtifactType.Review));

        if (onProgress is not null)
            await onProgress($"Reviewing {requirements.Items.Count} requirements against the proposal…");

        var text = new System.Text.StringBuilder();
        await foreach (var evt in ai.StreamAsync(request, ct))
            if (evt is AiStreamEvent.Delta d)
                text.Append(d.Text);

        var rows = ModelJson.ParseArray<ReviewRow>(text.ToString());
        if (rows.Count == 0)
            throw new InvalidOperationException("The model did not return a usable review.");

        // The requirements list is the report's skeleton: every requirement appears exactly
        // once, whether or not the model returned a row for it.
        var byId = new Dictionary<Guid, ReviewRow>();
        foreach (var row in rows)
            if (Guid.TryParse(row.RequirementId, out var id))
                byId.TryAdd(id, row);

        var payload = new ReviewPayload { GeneratedAt = DateTimeOffset.UtcNow };
        foreach (var requirement in requirements.Items)
        {
            var row = byId.GetValueOrDefault(requirement.Id);
            payload.Items.Add(new ReviewItem
            {
                RequirementId = requirement.Id,
                RequirementText = requirement.Text,
                RequirementType = requirement.Type,
                Coverage = row?.Coverage ?? ReviewCoverage.NotAddressed,
                WhereAddressed = row?.WhereAddressed,
                Improvement = row?.Improvement ?? (row is null ? "The review did not assess this requirement — run it again." : null),
                Risk = row?.Risk,
            });
        }
        return (operationId, payload);
    }
}
