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
            .Include(d => d.DocumentType)
            .Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<DocumentType>> GetTypesAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await LoadTypesAsync(db, proposalId, ct);
    }

    public async Task<DocumentType> AddTypeAsync(Guid proposalId, Guid userId, string name,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        name = name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("A document type needs a name.");

        var existing = await LoadTypesAsync(db, proposalId, ct);
        if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"This proposal already has a '{name}' type.");

        // New types append, so they rank below the ones already there - the order is the
        // priority the AI is told to resolve conflicts by.
        var type = new DocumentType
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Name = name,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(t => t.SortOrder) + 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync(ct);
        return type;
    }

    /// <summary>
    /// Removes an empty type added to this proposal. The two seeded categories stay whatever
    /// happens; a type holding material cannot be removed either - the documents would have
    /// nowhere to live - and neither can the last one, since every document needs a type.
    /// </summary>
    public async Task RemoveTypeAsync(Guid typeId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var type = await db.DocumentTypes.FirstAsync(t => t.Id == typeId, ct);
        await ProposalService.EnsureRoleAsync(db, type.ProposalId, userId, ProposalRole.Editor, ct);

        if (type.IsFixed)
            throw new InvalidOperationException(
                $"'{type.Name}' is one of the standard categories and is always available.");
        if (await db.Documents.AnyAsync(d => d.DocumentTypeId == typeId, ct))
            throw new InvalidOperationException(
                $"'{type.Name}' still holds material. Move or delete it first.");
        if (!await db.DocumentTypes.AnyAsync(t => t.ProposalId == type.ProposalId && t.Id != typeId, ct))
            throw new InvalidOperationException("A proposal needs at least one document type.");

        db.DocumentTypes.Remove(type);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Renames a document. Uploads start out named after the file they came from; the original
    /// file name is kept on the row, so a rename only changes what the material is called.
    /// </summary>
    public async Task RenameAsync(Guid documentId, Guid userId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Editor, ct);

        name = name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("A document needs a name.");
        if (document.Name == name) return;

        var now = DateTimeOffset.UtcNow;
        document.Name = name;
        // The name is part of what the AI is shown, so it counts as a material change.
        document.UpdatedAt = now;
        await MarkMaterialChangedAsync(db, document.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-files a document under a different type of the same proposal.</summary>
    public async Task SetDocumentTypeAsync(Guid documentId, Guid userId, Guid documentTypeId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        await ProposalService.EnsureRoleAsync(db, document.ProposalId, userId, ProposalRole.Editor, ct);
        if (document.DocumentTypeId == documentTypeId) return;

        if (!await db.DocumentTypes.AnyAsync(t => t.Id == documentTypeId && t.ProposalId == document.ProposalId, ct))
            throw new InvalidOperationException("That document type belongs to another proposal.");

        var now = DateTimeOffset.UtcNow;
        document.DocumentTypeId = documentTypeId;
        // The category is part of what the AI is shown, so re-filing counts as a material change.
        document.UpdatedAt = now;
        await MarkMaterialChangedAsync(db, document.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private static Task<List<DocumentType>> LoadTypesAsync(SagaDbContext db, Guid proposalId, CancellationToken ct)
        => db.DocumentTypes.Where(t => t.ProposalId == proposalId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);

    /// <summary>
    /// New material files under the type the caller picked, falling back to the proposal's first
    /// type - what the upload form pre-selects - so material is never left uncategorised.
    /// </summary>
    private static async Task<Guid> ResolveTypeAsync(SagaDbContext db, Guid proposalId, Guid? documentTypeId,
        CancellationToken ct)
    {
        if (documentTypeId is { } id)
        {
            if (!await db.DocumentTypes.AnyAsync(t => t.Id == id && t.ProposalId == proposalId, ct))
                throw new InvalidOperationException("That document type belongs to another proposal.");
            return id;
        }

        var types = await LoadTypesAsync(db, proposalId, ct);
        if (types.Count > 0) return types[0].Id;

        // Proposals left with an empty list still have to be able to take material.
        var defaults = DocumentType.CreateDefaults(proposalId, DateTimeOffset.UtcNow);
        db.DocumentTypes.AddRange(defaults);
        return defaults[0].Id;
    }

    public async Task<Document> UploadAsync(Guid proposalId, Guid userId, string fileName, Stream content,
        Guid? documentTypeId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);
        var typeId = await ResolveTypeAsync(db, proposalId, documentTypeId, ct);

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
            var extraction = await textExtractor.ExtractAsync(buffer, fileName,
                new AiCallContext(Guid.NewGuid(), AiOperation.ExtractDocument, proposalId, userId,
                    Label: Path.GetFileName(fileName)), ct);
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
            DocumentTypeId = typeId,
            Name = Path.GetFileName(fileName),
            OriginalFilePath = storagePath,
            OriginalFileName = Path.GetFileName(fileName),
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
        Guid? documentTypeId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);
        var typeId = await ResolveTypeAsync(db, proposalId, documentTypeId, ct);

        var now = DateTimeOffset.UtcNow;
        var note = new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Kind = DocumentKind.Note,
            DocumentTypeId = typeId,
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
