using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Prompts;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Proposal review: the user uploads the final proposal they produced outside Saga (as one or
/// more files per version) and gets it reviewed on criteria coverage, language, and general
/// quality. Uploads are review-only — they never join the generation/chat working context.
/// Version history is the list of uploaded versions; re-running the review on a version
/// replaces that version's report.
/// </summary>
public class ProposalReviewService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IFileStorage fileStorage,
    IDocumentTextExtractor textExtractor,
    IAiService ai,
    IConfiguration configuration)
{
    private sealed class CriteriaRow
    {
        public string? RequirementId { get; set; }
        public ReviewCoverage Coverage { get; set; }
        public string? WhereAddressed { get; set; }
        public string? Improvement { get; set; }
        public string? Risk { get; set; }
    }

    private sealed class ReviewResponse
    {
        public List<CriteriaRow> Criteria { get; set; } = [];
        public List<LanguageFinding> Language { get; set; } = [];
        public List<QualityFinding> Quality { get; set; } = [];
    }

    public async Task<List<FinalProposalVersion>> GetVersionsAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.FinalProposalVersions
            .Include(v => v.Files)
            .Include(v => v.CreatedBy)
            .Where(v => v.ProposalId == proposalId)
            .OrderByDescending(v => v.Number)
            .ToListAsync(ct);
    }

    public async Task<FinalProposalVersion> CreateVersionAsync(Guid proposalId, Guid userId,
        IReadOnlyList<(string FileName, Stream Content)> files, string? label = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("Upload at least one file.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        var number = await db.FinalProposalVersions
            .Where(v => v.ProposalId == proposalId)
            .Select(v => (int?)v.Number)
            .MaxAsync(ct) ?? 0;

        var version = new FinalProposalVersion
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Number = number + 1,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedById = userId,
            CreatedAt = now,
        };

        var storedPaths = new List<string>();
        try
        {
            foreach (var (fileName, content) in files)
            {
                // Buffer once: the original goes to storage, the same bytes go to extraction.
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, ct);

                buffer.Position = 0;
                var storagePath = await fileStorage.SaveAsync(proposalId, fileName, buffer, ct);
                storedPaths.Add(storagePath);

                buffer.Position = 0;
                var extraction = await textExtractor.ExtractAsync(buffer, fileName, ct);

                version.Files.Add(new FinalProposalFile
                {
                    Id = Guid.NewGuid(),
                    VersionId = version.Id,
                    Name = Path.GetFileName(fileName),
                    OriginalFilePath = storagePath,
                    ExtractedText = extraction.Text,
                    CreatedAt = now,
                });
            }
        }
        catch (Exception)
        {
            foreach (var path in storedPaths)
                await fileStorage.DeleteAsync(path, CancellationToken.None);
            throw;
        }

        db.FinalProposalVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return version;
    }

    public async Task DeleteVersionAsync(Guid versionId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.FinalProposalVersions.Include(v => v.Files)
            .FirstAsync(v => v.Id == versionId, ct);
        await ProposalService.EnsureRoleAsync(db, version.ProposalId, userId, ProposalRole.Editor, ct);

        foreach (var file in version.Files)
            if (file.OriginalFilePath is not null)
                await fileStorage.DeleteAsync(file.OriginalFilePath, ct);

        db.FinalProposalVersions.Remove(version);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs the three-axis review of one uploaded version and stores the report on that version.
    /// Like the draft review, it only reports — the user edits their source files themselves.
    /// </summary>
    public async Task<ProposalReviewPayload> ReviewAsync(Guid versionId, Guid userId,
        Func<string, Task>? onProgress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.FinalProposalVersions.Include(v => v.Files)
            .FirstAsync(v => v.Id == versionId, ct);
        await ProposalService.EnsureRoleAsync(db, version.ProposalId, userId, ProposalRole.Editor, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == version.ProposalId, ct);
        var requirementsArtifact = await db.Artifacts.FirstOrDefaultAsync(
            a => a.ProposalId == version.ProposalId && a.Type == ArtifactType.Requirements, ct);
        var requirements = RequirementsPayload.FromJson(requirementsArtifact?.ContentJson);

        var systemPrompt = ProposalReviewPrompts.BuildSystemPrompt(proposal, requirements);
        var context = ProposalReviewPrompts.BuildFilesContext(
            version.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        var request = new AiRequest(systemPrompt, [AiMessage.User(context)]);

        var run = new GenerationRun
        {
            Id = Guid.NewGuid(),
            ProposalId = version.ProposalId,
            ArtifactType = ArtifactType.Review,
            Model = "",
            StartedById = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
        };

        var stopwatch = Stopwatch.StartNew();
        var text = new System.Text.StringBuilder();
        try
        {
            if (onProgress is not null)
                await onProgress($"Reviewing version {version.Number} ({version.Files.Count} file{(version.Files.Count == 1 ? "" : "s")}) on criteria, language and quality…");
            await foreach (var evt in ai.StreamAsync(request, ct))
            {
                switch (evt)
                {
                    case AiStreamEvent.Delta d:
                        text.Append(d.Text);
                        break;
                    case AiStreamEvent.Completed c:
                        run.PromptTokens = c.PromptTokens;
                        run.CompletionTokens = c.CompletionTokens;
                        run.Model = c.Model;
                        run.EstimatedCost = UsageCost.Estimate(configuration, request.Tier,
                            c.PromptTokens, c.CompletionTokens);
                        run.Outcome = GenerationOutcome.Succeeded;
                        break;
                }
            }
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

        var response = ModelJson.ParseObject<ReviewResponse>(text.ToString())
            ?? throw new InvalidOperationException("The model did not return a usable review.");

        var payload = new ProposalReviewPayload
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Language = response.Language,
            Quality = response.Quality,
        };

        // The requirements list is the criteria section's skeleton: every requirement appears
        // exactly once, whether or not the model returned a row for it.
        var byId = new Dictionary<Guid, CriteriaRow>();
        foreach (var row in response.Criteria)
            if (Guid.TryParse(row.RequirementId, out var id))
                byId.TryAdd(id, row);

        foreach (var requirement in requirements.Items)
        {
            var row = byId.GetValueOrDefault(requirement.Id);
            payload.Criteria.Add(new ReviewItem
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

        version.ReviewJson = payload.ToJson();
        version.ReviewedAt = payload.GeneratedAt;
        await db.SaveChangesAsync(ct);
        return payload;
    }
}
