using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;
using System.Text.Json;

namespace Saga.Infrastructure.Services;

public class DocumentService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IFileStorage fileStorage,
    IDocumentTextExtractor textExtractor)
{
    public async Task<List<Document>> GetForProposalAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.Documents
            .Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Document> UploadAsync(Guid proposalId, Guid userId, string fileName, Stream content,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        // Buffer once: the original goes to storage, the same bytes go to extraction.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        buffer.Position = 0;
        var storagePath = await fileStorage.SaveAsync(proposalId, fileName, buffer, ct);

        string extractedText;
        string? pageMapJson = null;
        try
        {
            buffer.Position = 0;
            var extraction = await textExtractor.ExtractAsync(buffer, fileName, ct);
            extractedText = extraction.Text;
            pageMapJson = JsonSerializer.Serialize(extraction.Pages);
        }
        catch (Exception)
        {
            await fileStorage.DeleteAsync(storagePath, ct);
            throw;
        }

        var now = DateTimeOffset.UtcNow;
        var document = new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Kind = DocumentKind.Upload,
            Name = Path.GetFileName(fileName),
            OriginalFilePath = storagePath,
            ExtractedText = extractedText,
            PageMapJson = pageMapJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Documents.Add(document);
        db.DocumentVersions.Add(NewVersion(document.Id, extractedText, VersionOrigin.Generated, userId, now));
        await MarkMaterialChangedAsync(db, proposalId, now, ct);
        await db.SaveChangesAsync(ct);
        return document;
    }

    /// <summary>
    /// Saves a manual edit of an upload's extracted text (e.g. removing boilerplate sections).
    /// Every save is snapshotted to the document's version history.
    /// </summary>
    public async Task UpdateExtractedTextAsync(Guid documentId, Guid userId, string text,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId && d.Kind == DocumentKind.Upload, ct);
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        document.ExtractedText = text;
        document.PageMapJson = null;   // Page offsets no longer match the edited text.
        document.CondensedText = null; // Re-condense from the edited text when needed.
        document.UpdatedAt = now;
        db.DocumentVersions.Add(NewVersion(document.Id, text, VersionOrigin.Edited, userId, now));
        await MarkMaterialChangedAsync(db, document.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<DocumentVersion>> GetVersionsAsync(Guid documentId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Reader, ct);
        return await db.DocumentVersions
            .Include(v => v.CreatedBy)
            .Where(v => v.DocumentId == documentId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task RestoreVersionAsync(Guid versionId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.DocumentVersions.Include(v => v.Document).FirstAsync(v => v.Id == versionId, ct);
        var document = version.Document!;
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        document.ExtractedText = version.Text;
        document.PageMapJson = null;   // The page map only ever matches the original extraction.
        document.CondensedText = null;
        document.UpdatedAt = now;
        db.DocumentVersions.Add(NewVersion(document.Id, version.Text, VersionOrigin.Restored, userId, now));
        await MarkMaterialChangedAsync(db, document.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private static DocumentVersion NewVersion(Guid documentId, string text, VersionOrigin origin,
        Guid userId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Text = text,
            Origin = origin,
            CreatedById = userId,
            CreatedAt = now,
        };

    public async Task<Document> AddNoteAsync(Guid proposalId, Guid userId, string title, string text,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        var note = new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Kind = DocumentKind.Note,
            Name = string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim(),
            ExtractedText = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Documents.Add(note);
        // Notes carry the same version history as uploads: the first save is the baseline.
        db.DocumentVersions.Add(NewVersion(note.Id, text, VersionOrigin.Generated, userId, now));
        await MarkMaterialChangedAsync(db, proposalId, now, ct);
        await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task UpdateNoteAsync(Guid documentId, Guid userId, string title, string text,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var note = await db.Documents.FirstAsync(d => d.Id == documentId && d.Kind == DocumentKind.Note, ct);
        await ProposalService.EnsureRoleAsync(db, note.ProposalId, userId, ProposalRole.Editor, ct);

        var now = DateTimeOffset.UtcNow;
        note.Name = string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim();
        note.ExtractedText = text;
        note.UpdatedAt = now;
        db.DocumentVersions.Add(NewVersion(note.Id, text, VersionOrigin.Edited, userId, now));
        await MarkMaterialChangedAsync(db, note.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid documentId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Editor, ct);

        if (document.OriginalFilePath is not null)
            await fileStorage.DeleteAsync(document.OriginalFilePath, ct);

        db.Documents.Remove(document);
        await MarkMaterialChangedAsync(db, document.ProposalId, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The material is the proposal's source, so any change bumps its last-activity stamp.</summary>
    private static async Task MarkMaterialChangedAsync(SagaDbContext db, Guid proposalId, DateTimeOffset now,
        CancellationToken ct)
    {
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        proposal.UpdatedAt = now;
    }
}
