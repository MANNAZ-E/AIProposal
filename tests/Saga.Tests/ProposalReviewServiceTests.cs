using System.Text;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ProposalReviewServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>, IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), $"saga-test-{Guid.NewGuid():N}");
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private ProposalReviewService CreateService()
        => new(db, new TempDirStorage(_storageDir),
            TestServices.Extractor(db, new FakeExtractor()), TestServices.Ai(db));

    public void Dispose()
    {
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId, RequirementsPayload Requirements)> SetupAsync(
        bool withRequirements = true)
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);

        var requirements = new RequirementsPayload
        {
            Items =
            [
                new RequirementItem { Text = "Submit before the deadline.", Type = RequirementType.Practical },
                new RequirementItem { Text = "Document comparable experience.", Type = RequirementType.Mandatory },
                new RequirementItem { Text = "Quality of the approach.", Type = RequirementType.Criterion },
            ],
        };
        if (withRequirements)
        {
            await using var setup = db.CreateDbContext();
            setup.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Type = ArtifactType.Requirements,
                Status = ArtifactStatus.Generated,
                ContentJson = requirements.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }
        return (elv, sda, proposalId, requirements);
    }

    private static (string, Stream) File(string name, string content = "final proposal text")
        => (name, new MemoryStream(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task Uploads_become_numbered_multi_file_versions()
    {
        var (elv, _, proposalId, _) = await SetupAsync();
        var service = CreateService();

        var first = await service.CreateVersionAsync(proposalId, elv,
            [File("proposal.pptx"), File("prices.xlsx")], "Final before QA");
        var second = await service.CreateVersionAsync(proposalId, elv, [File("proposal-v2.pptx")], null);

        Assert.Equal(1, first.Number);
        Assert.Equal("Final before QA", first.Label);
        Assert.Equal(2, first.Files.Count);
        Assert.All(first.Files, f => Assert.StartsWith("extracted: ", f.ExtractedText));
        Assert.Equal(2, second.Number);
        Assert.Null(second.Label);

        var versions = await service.GetVersionsAsync(proposalId, elv);
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.Equal(3, Directory.GetFiles(_storageDir, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Review_reports_criteria_language_and_quality_and_sticks_to_the_version()
    {
        var (elv, _, proposalId, requirements) = await SetupAsync();
        var service = CreateService();
        var version = await service.CreateVersionAsync(proposalId, elv, [File("proposal.pptx")]);

        var payload = await service.ReviewAsync(version.Id, elv);

        // Criteria: every requirement appears exactly once, joined by id.
        Assert.Equal(
            requirements.Items.Select(r => r.Id).ToHashSet(),
            payload.Criteria.Select(i => i.RequirementId).ToHashSet());
        Assert.Contains(payload.Criteria, i => i.Coverage == ReviewCoverage.Addressed);

        // Language and general quality both carry findings from the fake.
        Assert.NotEmpty(payload.Language);
        Assert.All(payload.Language, f => Assert.False(string.IsNullOrWhiteSpace(f.Suggestion)));
        Assert.NotEmpty(payload.Quality);
        Assert.All(payload.Quality, f =>
        {
            Assert.NotEmpty(f.Suggestions);
            Assert.False(string.IsNullOrWhiteSpace(f.RecommendedEdit));
        });

        // The report is stored on the version and round-trips through JSON.
        var stored = (await service.GetVersionsAsync(proposalId, elv)).Single(v => v.Id == version.Id);
        Assert.NotNull(stored.ReviewedAt);
        var restored = ProposalReviewPayload.FromJson(stored.ReviewJson);
        Assert.Equal(payload.Criteria.Count, restored.Criteria.Count);
        Assert.Equal(payload.Language.Count, restored.Language.Count);
        Assert.Equal(payload.Quality.Count, restored.Quality.Count);
    }

    [Fact]
    public async Task Review_without_requirements_still_reports_language_and_quality()
    {
        var (elv, _, proposalId, _) = await SetupAsync(withRequirements: false);
        var service = CreateService();
        var version = await service.CreateVersionAsync(proposalId, elv, [File("proposal.docx")]);

        var payload = await service.ReviewAsync(version.Id, elv);

        Assert.Empty(payload.Criteria);
        Assert.NotEmpty(payload.Language);
        Assert.NotEmpty(payload.Quality);
    }

    [Fact]
    public async Task Readers_cannot_upload_review_or_delete_and_delete_removes_stored_files()
    {
        var (elv, sda, proposalId, _) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var service = CreateService();
        var version = await service.CreateVersionAsync(proposalId, elv, [File("proposal.pptx")]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateVersionAsync(proposalId, sda, [File("x.pptx")]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewAsync(version.Id, sda));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteVersionAsync(version.Id, sda));

        // But a reader can see the versions.
        _ = await service.GetVersionsAsync(proposalId, sda);

        await service.DeleteVersionAsync(version.Id, elv);
        Assert.Empty(await service.GetVersionsAsync(proposalId, elv));
        Assert.Empty(Directory.GetFiles(_storageDir, "*", SearchOption.AllDirectories));
    }

    private sealed class FakeExtractor : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf", ".docx", ".pptx", ".xlsx" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => Task.FromResult(new ExtractionResult($"extracted: {fileName}", [new PageSpan(1, 0, 10)],
                new ExtractionUsage(MinimalPages: 0, BasicPages: 0, StandardPages: 3)));
    }
}
