using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public record LoadedContext(List<Document> Documents, List<Artifact> Artifacts, bool UseCondensed);

/// <summary>
/// Loads a proposal's material and artifacts for AI calls, applying the token-budget policy:
/// when the full material exceeds the budget, documents are AI-condensed once and the
/// condensed versions are used instead (spec: warn + fall back, no vector DB in v1).
/// </summary>
public class WorkingContextService(
    IDbContextFactory<SagaDbContext> dbFactory,
    CondensationService condensation,
    IConfiguration configuration)
{
    public int Budget => configuration.GetValue("AzureOpenAI:ContextTokenBudget", TokenBudget.DefaultBudget);

    /// <param name="userId">
    /// Who triggered the load. Condensation is an implicit AI call, so it needs an owner in the
    /// usage log; null when nothing user-initiated is behind it.
    /// </param>
    public async Task<LoadedContext> LoadAsync(Guid proposalId, Guid? userId = null,
        Func<string, Task>? onCondenseProgress = null, CancellationToken ct = default)
    {
        var (documents, artifacts) = await LoadRawAsync(proposalId, ct);

        var status = TokenBudget.Assess(documents, Budget);
        if (!status.OverBudget)
            return new LoadedContext(documents, artifacts, UseCondensed: false);

        await condensation.EnsureCondensedAsync(proposalId, userId, onCondenseProgress, ct);
        (documents, artifacts) = await LoadRawAsync(proposalId, ct);
        return new LoadedContext(documents, artifacts, UseCondensed: true);
    }

    /// <summary>
    /// Loads only the material a chat froze at its start. The budget is assessed against that
    /// subset, so unchecking a large appendix genuinely avoids condensing the whole proposal.
    /// </summary>
    public async Task<LoadedContext> LoadForSelectionAsync(Guid proposalId, MaterialSelection selection,
        Guid? userId = null, CancellationToken ct = default)
    {
        var (documents, artifacts) = await LoadRawAsync(proposalId, ct);
        var chosenDocuments = Filter(documents, selection);
        var chosenArtifacts = Filter(artifacts, selection);

        var status = TokenBudget.Assess(chosenDocuments, Budget);
        if (!status.OverBudget)
            return new LoadedContext(chosenDocuments, chosenArtifacts, UseCondensed: false);

        await condensation.EnsureCondensedAsync(proposalId, userId, null, ct);
        (documents, artifacts) = await LoadRawAsync(proposalId, ct);
        return new LoadedContext(Filter(documents, selection), Filter(artifacts, selection), UseCondensed: true);
    }

    private static List<Document> Filter(List<Document> documents, MaterialSelection selection)
    {
        var ids = selection.DocumentIds.ToHashSet();
        return documents.Where(d => ids.Contains(d.Id)).ToList();
    }

    private static List<Artifact> Filter(List<Artifact> artifacts, MaterialSelection selection)
    {
        var types = selection.ArtifactTypes.ToHashSet();
        return artifacts.Where(a => types.Contains(a.Type)).ToList();
    }

    public async Task<BudgetStatus> AssessAsync(Guid proposalId, CancellationToken ct = default)
    {
        var (documents, _) = await LoadRawAsync(proposalId, ct);
        return TokenBudget.Assess(documents, Budget);
    }

    private async Task<(List<Document>, List<Artifact>)> LoadRawAsync(Guid proposalId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The document type is part of the prompt: WorkingContextBuilder groups material by it.
        var documents = await db.Documents.Include(d => d.DocumentType)
            .Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var artifacts = await db.Artifacts.Where(a => a.ProposalId == proposalId).ToListAsync(ct);
        return (documents, artifacts);
    }
}
