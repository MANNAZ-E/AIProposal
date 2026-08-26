using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>Roll-up across a set of calls. Costs are USD; the UI converts for display.</summary>
/// <param name="Pages">
/// Billed pages across all three Content Understanding meters. They are summed only for display —
/// each row was priced at its own meter's rate before it was ever added up.
/// </param>
public record AiUsageTotals(int Calls, long InputTokens, long OutputTokens, long CachedInputTokens,
    long Pages, decimal CostUsd)
{
    public static readonly AiUsageTotals Empty = new(0, 0, 0, 0, 0, 0m);
}

/// <summary>One line of the service → model breakdown.</summary>
public record AiUsageBreakdownRow(AiServiceKind Service, string Model, AiUsageTotals Totals);

/// <summary>
/// One meter's slice of the extraction bill. The cost is the frozen per-row figures summed, never
/// re-derived from <paramref name="Pages"/> at today's rates — that is what keeps the two
/// per-service sections adding up to the same total the summary card shows.
/// </summary>
public record AiMeterRow(string Meter, int Calls, long Pages, long ContextualizationTokens,
    decimal CostUsd);

/// <summary>One call in the log. Payloads are deliberately absent — they are fetched per call.</summary>
public record AiUsageCall(Guid Id, Guid OperationId, DateTimeOffset StartedAt, AiServiceKind Service,
    string Model, AiOperation Operation, string? Label, string? UserName, int InputTokens,
    int CachedInputTokens, int OutputTokens, int? MinimalPages, int? BasicPages, int? StandardPages, decimal CostUsd,
    TimeSpan Duration, GenerationOutcome Outcome, string? ErrorMessage)
{
    /// <summary>Null on an LLM call, and where the analyzer reported no quantities at all.</summary>
    public int? Pages => MinimalPages + BasicPages + StandardPages;

    /// <summary>The meter this call was charged on. See <see cref="AiUsageService.MeterLabel"/>.</summary>
    public string? Meter => AiUsageService.MeterLabel(MinimalPages, BasicPages, StandardPages);
}

/// <summary>A call plus the stored request and response, for backtracking what happened.</summary>
public record AiUsageCallDetail(AiUsageCall Call, string? InstructionText, string? RequestText,
    string? ResponseText);

public record ProposalUsage(AiUsageTotals Totals, List<AiUsageBreakdownRow> Breakdown,
    List<AiMeterRow> Meters);

/// <summary>
/// Reads the AI spend log: per-proposal roll-ups for the workspace Usage tab, the individual
/// call log, and the stored payload of a single call. Writing is the decorators' job.
/// </summary>
public class AiUsageService(IDbContextFactory<SagaDbContext> dbFactory, PricingService pricing)
{
    /// <summary>The call was charged, but the analyzer never told us on which meter.</summary>
    private const string NotReported = "Not reported";

    /// <summary>Cheapest meter first, then the two rows that stand in for an absent number.</summary>
    private static readonly string[] MeterRowOrder =
        ["Minimal", "Basic", "Standard", "Mixed", "None", NotReported];

    /// <summary>DKK per USD for display; 0 means show USD unconverted.</summary>
    public decimal UsdToDkk => pricing.UsdToDkk;

    /// <summary>
    /// Which meter a Content Understanding call was charged on — the thing that decides the rate,
    /// and the only reason the same analyzer can cost 500× more for one upload than another. Null
    /// when the analyzer reported nothing at all; "None" when it reported zero of every meter,
    /// which is a different fact; "Mixed" if one call somehow spanned two. Shared with the
    /// per-meter roll-up, because deriving this rule twice is how a table and the log beneath it
    /// drift apart.
    /// </summary>
    public static string? MeterLabel(int? minimal, int? basic, int? standard)
    {
        if (minimal is null && basic is null && standard is null) return null;

        var charged = new List<string>(3);
        if (minimal > 0) charged.Add("Minimal");
        if (basic > 0) charged.Add("Basic");
        if (standard > 0) charged.Add("Standard");
        return charged.Count switch
        {
            0 => "None",
            1 => charged[0],
            _ => "Mixed",
        };
    }

    /// <summary>Totals, the service → model breakdown, and the extraction meters for one proposal.</summary>
    public async Task<ProposalUsage> GetProposalUsageAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureReadAccessAsync(db, proposalId, userId, ct);

        var breakdown = await BreakdownAsync(db, r => r.ProposalId == proposalId, ct);
        var meters = await MetersAsync(db, r => r.ProposalId == proposalId, ct);
        return new ProposalUsage(Sum(breakdown.Select(b => b.Totals)), breakdown, meters);
    }

    /// <summary>The proposal's calls, newest first. Never projects the payload columns.</summary>
    public async Task<List<AiUsageCall>> GetProposalCallsAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureReadAccessAsync(db, proposalId, userId, ct);

        return await db.AiUsage
            .Where(r => r.ProposalId == proposalId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new AiUsageCall(r.Id, r.OperationId, r.StartedAt, r.Service, r.Model,
                r.Operation, r.Label, r.StartedBy!.DisplayName, r.InputTokens,
                r.CachedInputTokens, r.OutputTokens, r.MinimalPages, r.BasicPages, r.StandardPages,
                r.EstimatedCostUsd, r.Duration,
                r.Outcome, r.ErrorMessage))
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
            await ProposalService.EnsureReadAccessAsync(db, proposalId, userId, ct);

        var call = new AiUsageCall(record.Id, record.OperationId, record.StartedAt, record.Service,
            record.Model, record.Operation, record.Label, record.StartedBy?.DisplayName,
            record.InputTokens, record.CachedInputTokens, record.OutputTokens, record.MinimalPages,
            record.BasicPages, record.StandardPages, record.EstimatedCostUsd, record.Duration,
            record.Outcome, record.ErrorMessage);
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

    /// <summary>Extraction spend by meter across every proposal, for the admin page.</summary>
    public async Task<List<AiMeterRow>> GetGlobalMetersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await MetersAsync(db, _ => true, ct);
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
                // Nulls collapse to 0 for the roll-up; the per-call log is where "not reported"
                // stays visible as a dash.
                Pages = g.Sum(r => (long)((r.MinimalPages ?? 0) + (r.BasicPages ?? 0)
                    + (r.StandardPages ?? 0))),
                CostUsd = g.Sum(r => r.EstimatedCostUsd),
            })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.Service)
            .ThenByDescending(r => r.CostUsd)
            .ThenBy(r => r.Model)
            .Select(r => new AiUsageBreakdownRow(r.Service, r.Model,
                new AiUsageTotals(r.Calls, r.InputTokens, r.OutputTokens, r.CachedInputTokens,
                    r.Pages, r.CostUsd)))
            .ToList();
    }

    /// <summary>
    /// Extraction spend split by the meter it was charged on — the breakdown that explains the
    /// bill, since the meter and not the analyzer sets the rate. Grouped in memory rather than in
    /// SQL because the meter is a derived label, and the alternative is to restate
    /// <see cref="MeterLabel"/> as a group key. The projection is five columns wide and Content
    /// Understanding writes one row per upload plus one per embedded figure, so there is little
    /// to carry.
    /// </summary>
    private static async Task<List<AiMeterRow>> MetersAsync(SagaDbContext db,
        System.Linq.Expressions.Expression<Func<AiUsageRecord, bool>> filter, CancellationToken ct)
    {
        var rows = await db.AiUsage
            .Where(filter)
            .Where(r => r.Service == AiServiceKind.ContentUnderstanding)
            .Select(r => new
            {
                r.MinimalPages,
                r.BasicPages,
                r.StandardPages,
                r.ContextualizationTokens,
                r.EstimatedCostUsd,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => MeterLabel(r.MinimalPages, r.BasicPages, r.StandardPages) ?? NotReported)
            .Select(g => new AiMeterRow(
                g.Key,
                g.Count(),
                g.Sum(r => (long)((r.MinimalPages ?? 0) + (r.BasicPages ?? 0)
                    + (r.StandardPages ?? 0))),
                g.Sum(r => (long)(r.ContextualizationTokens ?? 0)),
                g.Sum(r => r.EstimatedCostUsd)))
            .OrderBy(r => Array.IndexOf(MeterRowOrder, r.Meter))
            .ThenBy(r => r.Meter)
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
                list.Sum(t => t.Pages),
                list.Sum(t => t.CostUsd));
    }
}
