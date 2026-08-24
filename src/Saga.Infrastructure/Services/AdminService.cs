using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>Spend for one proposal, as shown on the admin roll-up.</summary>
public record ProposalSpend(Guid ProposalId, string Title, string ClientName, int Calls,
    long InputTokens, long CachedInputTokens, long OutputTokens, long PageCount, decimal CostUsd,
    DateTimeOffset? LastCallAt);

/// <summary>One row in the admin recycle bin.</summary>
public record DeletedProposal(Guid ProposalId, string Title, string ClientName, string OwnerName,
    DateTimeOffset? DeletedAt);

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

    /// <summary>Every soft-deleted proposal, across all users, newest deletion first.</summary>
    public async Task<List<DeletedProposal>> GetDeletedProposalsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Proposals
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new DeletedProposal(p.Id, p.Title, p.ClientName, p.Owner!.DisplayName, p.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>Spend per proposal, newest activity first. Includes archived proposals and chat.</summary>
    public async Task<List<ProposalSpend>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AiUsage
            .Where(r => r.ProposalId != null)
            .GroupBy(r => new { r.ProposalId, r.Proposal!.Title, r.Proposal.ClientName })
            .Select(g => new
            {
                g.Key.ProposalId,
                g.Key.Title,
                g.Key.ClientName,
                Calls = g.Count(),
                InputTokens = g.Sum(r => (long)r.InputTokens),
                CachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                OutputTokens = g.Sum(r => (long)r.OutputTokens),
                PageCount = g.Sum(r => (long)r.PageCount),
                CostUsd = g.Sum(r => r.EstimatedCostUsd),
                LastCallAt = g.Max(r => (DateTimeOffset?)r.StartedAt),
            })
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.LastCallAt)
            .Select(r => new ProposalSpend(r.ProposalId!.Value, r.Title, r.ClientName, r.Calls,
                r.InputTokens, r.CachedInputTokens, r.OutputTokens, r.PageCount, r.CostUsd,
                r.LastCallAt))
            .ToList();
    }
}
