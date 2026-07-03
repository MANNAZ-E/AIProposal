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
    public async Task Material_change_marks_generated_artifacts_stale_but_not_locked_ones()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var service = CreateService();

        await using (var setup = db.CreateDbContext())
        {
            setup.Artifacts.AddRange(
                new Artifact { Id = Guid.NewGuid(), ProposalId = proposalId, Type = ArtifactType.Summary, Status = ArtifactStatus.Generated, UpdatedAt = DateTimeOffset.UtcNow },
                new Artifact { Id = Guid.NewGuid(), ProposalId = proposalId, Type = ArtifactType.Scoping, Status = ArtifactStatus.Generated, IsLocked = true, UpdatedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }

        await service.AddNoteAsync(proposalId, elv, "n", "x");

        await using var check = db.CreateDbContext();
        var summary = check.Artifacts.Single(a => a.ProposalId == proposalId && a.Type == ArtifactType.Summary);
        var scoping = check.Artifacts.Single(a => a.ProposalId == proposalId && a.Type == ArtifactType.Scoping);
        Assert.True(summary.IsStale);
        Assert.False(scoping.IsStale);
    }

    private sealed class TempDirStorage(string root) : IFileStorage
    {
        public async Task<string> SaveAsync(Guid proposalId, string fileName, Stream content, CancellationToken ct = default)
        {
            var relative = Path.Combine(proposalId.ToString("N"), $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}");
            var fullPath = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var file = File.Create(fullPath);
            await content.CopyToAsync(file, ct);
            return relative.Replace('\\', '/');
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(File.OpenRead(Path.Combine(root, path)));

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            var fullPath = Path.Combine(root, path);
            if (File.Exists(fullPath)) File.Delete(fullPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExtractor : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf", ".txt" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName, CancellationToken ct = default)
            => Task.FromResult(new ExtractionResult($"extracted: {fileName}", [new PageSpan(1, 0, 10)]));
    }

    private sealed class ThrowingExtractor : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName, CancellationToken ct = default)
            => throw new InvalidOperationException("extraction failed");
    }
}
