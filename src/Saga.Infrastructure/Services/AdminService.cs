using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public record ProposalUsage(Guid ProposalId, string Title, string ClientName, int Runs,
    long PromptTokens, long CompletionTokens, decimal EstimatedCost, DateTimeOffset? LastRunAt);

/// <summary>Mannaz voice settings + the usage/cost view (spec: usage logging with simple view).</summary>
public class AdminService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<MannazVoiceSettings> GetVoiceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MannazVoiceSettings.FirstAsync(ct);
    }

    public async Task SaveVoiceAsync(string toneOfVoice, string aboutMannaz, string terminology,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var voice = await db.MannazVoiceSettings.FirstAsync(ct);
        voice.ToneOfVoice = toneOfVoice.Trim();
        voice.AboutMannaz = aboutMannaz.Trim();
        voice.Terminology = terminology.Trim();
        voice.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Spend per proposal, newest activity first. Includes archived and chat runs.</summary>
    public async Task<List<ProposalUsage>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GenerationRuns
            .GroupBy(r => new { r.ProposalId, r.Proposal!.Title, r.Proposal.ClientName })
            .Select(g => new
            {
                g.Key.ProposalId,
                g.Key.Title,
                g.Key.ClientName,
                Runs = g.Count(),
                PromptTokens = g.Sum(r => (long)r.PromptTokens),
                CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                Cost = g.Sum(r => r.EstimatedCost),
                LastRunAt = g.Max(r => (DateTimeOffset?)r.StartedAt),
            })
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.LastRunAt)
            .Select(r => new ProposalUsage(r.ProposalId, r.Title, r.ClientName, r.Runs,
                r.PromptTokens, r.CompletionTokens, r.Cost, r.LastRunAt))
            .ToList();
    }
}
