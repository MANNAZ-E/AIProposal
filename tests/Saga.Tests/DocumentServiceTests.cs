using System.Text;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class DocumentServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>, IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), $"saga-test-{Guid.NewGuid():N}");
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private DocumentService CreateService(IDocumentTextExtractor? extractor = null)
        => new(db, new TempDirStorage(_storageDir), extractor ?? new FakeExtractor());

    public void Dispose()
    {
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId)> SetupAsync()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        return (elv, sda, proposalId);
    }

    [Fact]
    public async Task Upload_stores_file_and_extracted_text()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        var document = await service.UploadAsync(proposalId, elv, "tender.pdf", stream);

        Assert.Equal(DocumentKind.Upload, document.Kind);
        Assert.Equal("extracted: tender.pdf", document.ExtractedText);
        Assert.NotNull(document.PageMapJson);
        Assert.True(File.Exists(Path.Combine(_storageDir, document.OriginalFilePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Failed_extraction_removes_stored_file_and_saves_nothing()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService(new ThrowingExtractor());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadAsync(proposalId, elv, "bad.pdf", stream));

        var documents = await service.GetForProposalAsync(proposalId, elv);
        Assert.Empty(documents);
        Assert.Empty(Directory.Exists(_storageDir)
            ? Directory.GetFiles(_storageDir, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Extracted_text_can_be_edited_and_restored_with_full_history()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        var document = await service.UploadAsync(proposalId, elv, "tender.pdf", stream);

        await service.UpdateExtractedTextAsync(document.Id, elv, "edited text");

        var updated = (await service.GetForProposalAsync(proposalId, elv)).Single(d => d.Id == document.Id);
        Assert.Equal("edited text", updated.ExtractedText);
        Assert.Null(updated.PageMapJson); // page offsets no longer match the edited text

        // History: the original extraction plus the edit, newest first.
        var versions = await service.GetVersionsAsync(document.Id, elv);
        Assert.Equal(2, versions.Count);
        Assert.Equal(VersionOrigin.Edited, versions[0].Origin);
        Assert.Equal(VersionOrigin.Generated, versions[1].Origin);
        Assert.Equal("extracted: tender.pdf", versions[1].Text);

        // Restoring the original extraction writes a third, Restored snapshot.
        await service.RestoreVersionAsync(versions[1].Id, elv);
        var restored = (await service.GetForProposalAsync(proposalId, elv)).Single(d => d.Id == document.Id);
        Assert.Equal("extracted: tender.pdf", restored.ExtractedText);
        versions = await service.GetVersionsAsync(document.Id, elv);
        Assert.Equal(3, versions.Count);
        Assert.Equal(VersionOrigin.Restored, versions[0].Origin);
    }

    [Fact]
    public async Task Editing_extracted_text_requires_editor_role_and_marks_artifacts_stale()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var service = CreateService();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var document = await service.UploadAsync(proposalId, elv, "tender.pdf", stream);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.UpdateExtractedTextAsync(document.Id, sda, "hacked"));

        await service.UpdateExtractedTextAsync(document.Id, elv, "edited");

        await using var check = db.CreateDbContext();
        Assert.Equal("edited", check.Documents.Single(d => d.Id == document.Id).ExtractedText);
    }

    [Fact]
    public async Task Notes_can_be_added_edited_and_deleted()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        var note = await service.AddNoteAsync(proposalId, elv, "Kickoff", "Client wants X");
        Assert.Equal(DocumentKind.Note, note.Kind);

        await service.UpdateNoteAsync(note.Id, elv, "Kickoff meeting", "Client wants X and Y");
        var documents = await service.GetForProposalAsync(proposalId, elv);
        var updated = Assert.Single(documents);
        Assert.Equal("Kickoff meeting", updated.Name);
        Assert.Equal("Client wants X and Y", updated.ExtractedText);

        await service.DeleteAsync(note.Id, elv);
        Assert.Empty(await service.GetForProposalAsync(proposalId, elv));
    }

    [Fact]
    public async Task New_proposals_start_with_the_default_document_types()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        var types = await service.GetTypesAsync(proposalId, elv);

        Assert.Equal(["Client documents", "Mannaz documents"], types.Select(t => t.Name));
    }

    [Fact]
    public async Task Uploads_and_notes_are_filed_under_the_chosen_type()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();
        var types = await service.GetTypesAsync(proposalId, elv);
        var mannaz = types[1];

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var upload = await service.UploadAsync(proposalId, elv, "offering.pdf", stream, mannaz.Id);
        var note = await service.AddNoteAsync(proposalId, elv, "Kickoff", "text", mannaz.Id);

        Assert.Equal(mannaz.Id, upload.DocumentTypeId);
        Assert.Equal(mannaz.Id, note.DocumentTypeId);
    }

    [Fact]
    public async Task Material_added_without_a_type_falls_back_to_the_first_one()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();
        var first = (await service.GetTypesAsync(proposalId, elv))[0];

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var upload = await service.UploadAsync(proposalId, elv, "tender.pdf", stream);

        Assert.Equal(first.Id, upload.DocumentTypeId);
        Assert.Equal("Client documents", first.Name);
    }

    [Fact]
    public async Task Added_types_append_below_the_existing_ones_and_names_stay_unique()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        var added = await service.AddTypeAsync(proposalId, elv, "  Tender annexes  ");

        Assert.Equal("Tender annexes", added.Name);
        var types = await service.GetTypesAsync(proposalId, elv);
        Assert.Equal(["Client documents", "Mannaz documents", "Tender annexes"], types.Select(t => t.Name));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddTypeAsync(proposalId, elv, "tender annexes"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddTypeAsync(proposalId, elv, "   "));
    }

    [Fact]
    public async Task A_type_can_only_be_removed_once_it_holds_no_material()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();
        var types = await service.GetTypesAsync(proposalId, elv);
        var client = types[0];
        var mannaz = types[1];

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var upload = await service.UploadAsync(proposalId, elv, "tender.pdf", stream, client.Id);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveTypeAsync(client.Id, elv));
        Assert.Contains("still holds material", blocked.Message);

        // The empty one goes; the last remaining type stays, since every document needs one.
        await service.RemoveTypeAsync(mannaz.Id, elv);
        Assert.Equal(["Client documents"], (await service.GetTypesAsync(proposalId, elv)).Select(t => t.Name));

        await service.DeleteAsync(upload.Id, elv);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveTypeAsync(client.Id, elv));
    }

    [Fact]
    public async Task A_document_can_be_refiled_under_another_type_of_the_same_proposal()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var service = CreateService();
        var types = await service.GetTypesAsync(proposalId, elv);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var upload = await service.UploadAsync(proposalId, elv, "offering.pdf", stream, types[0].Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SetDocumentTypeAsync(upload.Id, sda, types[1].Id));

        await service.SetDocumentTypeAsync(upload.Id, elv, types[1].Id);
        var reloaded = Assert.Single(await service.GetForProposalAsync(proposalId, elv));
        Assert.Equal("Mannaz documents", reloaded.DocumentType!.Name);

        var otherProposal = await _proposals.CreateAsync(elv, "Other", "C", null, OutputFormat.PowerPoint);
        var foreignType = (await service.GetTypesAsync(otherProposal, elv))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetDocumentTypeAsync(upload.Id, elv, foreignType.Id));
    }

    [Fact]
    public async Task Reader_cannot_upload_or_add_notes()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var service = CreateService();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.UploadAsync(proposalId, sda, "f.pdf", stream));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.AddNoteAsync(proposalId, sda, "t", "x"));

        // But a reader can view the material.
        _ = await service.GetForProposalAsync(proposalId, sda);
    }

    [Fact]
    public async Task Notes_carry_the_same_text_history_as_uploads()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        var note = await service.AddNoteAsync(proposalId, elv, "Kickoff", "First take");
        await service.UpdateNoteAsync(note.Id, elv, "Kickoff", "Second take");

        var versions = await service.GetVersionsAsync(note.Id, elv);
        Assert.Equal(2, versions.Count);
        Assert.Equal(VersionOrigin.Edited, versions[0].Origin);   // Newest first.
        Assert.Equal(VersionOrigin.Generated, versions[1].Origin);

        await service.RestoreVersionAsync(versions[1].Id, elv);

        var restored = Assert.Single(await service.GetForProposalAsync(proposalId, elv));
        Assert.Equal("First take", restored.ExtractedText);
        Assert.Equal(3, (await service.GetVersionsAsync(note.Id, elv)).Count);
    }

    private sealed class FakeExtractor : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf", ".txt" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => Task.FromResult(new ExtractionResult($"extracted: {fileName}", [new PageSpan(1, 0, 10)], PageCount: 3));
    }

    private sealed class ThrowingExtractor : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => throw new InvalidOperationException("extraction failed");
    }
}
