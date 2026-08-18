using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Data;
using Saga.Infrastructure.Export;

namespace Saga.Infrastructure.Services;

public record ExportFile(string FileName, string ContentType, byte[] Bytes);

public record ExportReadiness(bool HasStructure, bool HasContent, int MissingUnits)
{
    public bool CanExport => HasStructure && HasContent;
}

/// <summary>
/// Exports the approved structure + content to PPTX or DOCX (spec §17). Readers can export.
/// Styling is the clean Saga fallback until Emil's sample Mannaz deck arrives to mimic.
/// </summary>
public class ExportService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<ExportReadiness> GetReadinessAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        var artifacts = await db.Artifacts.Where(a => a.ProposalId == proposalId).ToListAsync(ct);

        var structure = StructurePayload.FromJson(
            artifacts.FirstOrDefault(a => a.Type == ArtifactType.Structure)?.ContentJson);
        var content = ContentPayload.FromJson(
            artifacts.FirstOrDefault(a => a.Type == ArtifactType.Content)?.ContentJson);

        var covered = content.Units.Select(u => u.StructureItemId).ToHashSet();
        return new ExportReadiness(
            HasStructure: structure.Items.Count > 0,
            HasContent: content.Units.Count > 0,
            MissingUnits: structure.Items.Count(i => !covered.Contains(i.Id)));
    }

    public async Task<ExportFile> ExportAsync(Guid proposalId, Guid userId, OutputFormat format,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var artifacts = await db.Artifacts
            .Where(a => a.ProposalId == proposalId
                        && (a.Type == ArtifactType.Structure || a.Type == ArtifactType.Content))
            .ToListAsync(ct);
        var structure = StructurePayload.FromJson(
            artifacts.FirstOrDefault(a => a.Type == ArtifactType.Structure)?.ContentJson);
        var content = ContentPayload.FromJson(
            artifacts.FirstOrDefault(a => a.Type == ArtifactType.Content)?.ContentJson);

        if (structure.Items.Count == 0)
            throw new InvalidOperationException("There is no structure to export. Generate the structure and content first.");
        if (content.Units.Count == 0)
            throw new InvalidOperationException("There is no content to export. Generate the content first.");

        var baseName = SafeFileName($"{proposal.ClientName} - {proposal.Title}");
        return format == OutputFormat.PowerPoint
            ? new ExportFile($"{baseName}.pptx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                PptxExporter.Build(proposal, structure, content))
            : new ExportFile($"{baseName}.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                DocxExporter.Build(proposal, structure, content));
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
