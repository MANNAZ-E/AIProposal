using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Domain;
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

    public async Task<BudgetStatus> AssessAsync(Guid proposalId, CancellationToken ct = default)
    {
        var (documents, _) = await LoadRawAsync(proposalId, ct);
        return TokenBudget.Assess(documents, Budget);
    }

    private async Task<(List<Document>, List<Artifact>)> LoadRawAsync(Guid proposalId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var documents = await db.Documents.Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var artifacts = await db.Artifacts.Where(a => a.ProposalId == proposalId).ToListAsync(ct);
        return (documents, artifacts);
    }
}
