using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>Roll-up across a set of calls. Costs are USD; the UI converts for display.</summary>
public record AiUsageTotals(int Calls, long InputTokens, long OutputTokens, long CachedInputTokens,
    long PageCount, decimal CostUsd)
{
    public static readonly AiUsageTotals Empty = new(0, 0, 0, 0, 0, 0m);
}

/// <summary>One line of the service → model breakdown.</summary>
public record AiUsageBreakdownRow(AiServiceKind Service, string Model, AiUsageTotals Totals);

/// <summary>One call in the log. Payloads are deliberately absent — they are fetched per call.</summary>
public record AiUsageCall(Guid Id, Guid OperationId, DateTimeOffset StartedAt, AiServiceKind Service,
    string Model, AiOperation Operation, string? Label, string? UserName, int InputTokens,
    int OutputTokens, int PageCount, decimal CostUsd, TimeSpan Duration, GenerationOutcome Outcome,
    string? ErrorMessage);

/// <summary>A call plus the stored request and response, for backtracking what happened.</summary>
public record AiUsageCallDetail(AiUsageCall Call, string? InstructionText, string? RequestText,
    string? ResponseText);

public record ProposalUsage(AiUsageTotals Totals, List<AiUsageBreakdownRow> Breakdown);

/// <summary>
/// Reads the AI spend log: per-proposal roll-ups for the workspace Usage tab, the individual
/// call log, and the stored payload of a single call. Writing is the decorators' job.
/// </summary>
public class AiUsageService(IDbContextFactory<SagaDbContext> dbFactory, PricingService pricing)
{
    /// <summary>DKK per USD for display; 0 means show USD unconverted.</summary>
    public decimal UsdToDkk => pricing.UsdToDkk;

    /// <summary>Totals plus the service → model breakdown for one proposal.</summary>
    public async Task<ProposalUsage> GetProposalUsageAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var breakdown = await BreakdownAsync(db, r => r.ProposalId == proposalId, ct);
        return new ProposalUsage(Sum(breakdown.Select(b => b.Totals)), breakdown);
    }

    /// <summary>The proposal's calls, newest first. Never projects the payload columns.</summary>
    public async Task<List<AiUsageCall>> GetProposalCallsAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        return await db.AiUsage
            .Where(r => r.ProposalId == proposalId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new AiUsageCall(r.Id, r.OperationId, r.StartedAt, r.Service, r.Model,
                r.Operation, r.Label, r.StartedBy!.DisplayName, r.InputTokens, r.OutputTokens,
                r.PageCount, r.EstimatedCostUsd, r.Duration, r.Outcome, r.ErrorMessage))
            .ToListAsync(ct);
    }

    /// <summary>One call including the stored request and response.</summary>
    public async Task<AiUsageCallDetail?> GetCallDetailAsync(Guid recordId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.AiUsage.Include(r => r.StartedBy)
            .FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return null;

        if (record.ProposalId is { } proposalId)
            await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var call = new AiUsageCall(record.Id, record.OperationId, record.StartedAt, record.Service,
            record.Model, record.Operation, record.Label, record.StartedBy?.DisplayName,
            record.InputTokens, record.OutputTokens, record.PageCount, record.EstimatedCostUsd,
            record.Duration, record.Outcome, record.ErrorMessage);
        return new AiUsageCallDetail(call, record.InstructionText, record.RequestText, record.ResponseText);
    }

    /// <summary>
    /// The user discarded a generation in the diff review. Every call of the operation is marked
    /// — the money was spent regardless, so the rows still count toward spend.
    /// </summary>
    public async Task MarkOperationRejectedAsync(Guid operationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AiUsage
            .Where(r => r.OperationId == operationId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Outcome, GenerationOutcome.Rejected), ct);
    }

    /// <summary>Spend across every proposal, for the admin page.</summary>
    public async Task<List<AiUsageBreakdownRow>> GetGlobalBreakdownAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await BreakdownAsync(db, _ => true, ct);
    }

    private static async Task<List<AiUsageBreakdownRow>> BreakdownAsync(SagaDbContext db,
        System.Linq.Expressions.Expression<Func<AiUsageRecord, bool>> filter, CancellationToken ct)
    {
        var rows = await db.AiUsage
            .Where(filter)
            .GroupBy(r => new { r.Service, r.Model })
            .Select(g => new
            {
                g.Key.Service,
                g.Key.Model,
                Calls = g.Count(),
                InputTokens = g.Sum(r => (long)r.InputTokens),
                OutputTokens = g.Sum(r => (long)r.OutputTokens),
                CachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                PageCount = g.Sum(r => (long)r.PageCount),
                CostUsd = g.Sum(r => r.EstimatedCostUsd),
            })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.Service)
            .ThenByDescending(r => r.CostUsd)
            .ThenBy(r => r.Model)
            .Select(r => new AiUsageBreakdownRow(r.Service, r.Model,
                new AiUsageTotals(r.Calls, r.InputTokens, r.OutputTokens, r.CachedInputTokens,
                    r.PageCount, r.CostUsd)))
            .ToList();
    }

    public static AiUsageTotals Sum(IEnumerable<AiUsageTotals> totals)
    {
        var list = totals as ICollection<AiUsageTotals> ?? totals.ToList();
        return list.Count == 0
            ? AiUsageTotals.Empty
            : new AiUsageTotals(
                list.Sum(t => t.Calls),
                list.Sum(t => t.InputTokens),
                list.Sum(t => t.OutputTokens),
                list.Sum(t => t.CachedInputTokens),
                list.Sum(t => t.PageCount),
                list.Sum(t => t.CostUsd));
    }
}
