using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>
/// Covers the usage decorators: every paid call — LLM or document extraction — lands in AiUsage
/// with the units it was billed on, and multi-call operations stay grouped.
/// </summary>
public class AiUsageTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>, IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), $"saga-test-{Guid.NewGuid():N}");
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    public void Dispose()
    {
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private async Task<(Guid ElvId, Guid ProposalId)> SetupAsync(string? material = "Client tender material.")
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        if (material is not null)
        {
            await using var setup = db.CreateDbContext();
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                DocumentTypeId = await TestServices.DefaultDocumentTypeAsync(db, proposalId),
                Kind = DocumentKind.Upload,
                Name = "tender.pdf",
                ExtractedText = material,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }
        return (elv, proposalId);
    }

    [Fact]
    public async Task Generation_records_the_call_with_its_payload_and_cost()
    {
        var (elv, proposalId) = await SetupAsync();

        var result = await TestServices.Generation(db)
            .GenerateAsync(proposalId, ArtifactType.Summary, elv, "keep it short", null);

        await using var check = db.CreateDbContext();
        var record = check.AiUsage.Single(r => r.OperationId == result.OperationId);

        Assert.Equal(AiServiceKind.AzureOpenAI, record.Service);
        Assert.Equal(AiOperation.GenerateArtifact, record.Operation);
        Assert.Equal(ArtifactType.Summary, record.ArtifactType);
        Assert.Equal("fake-model", record.Model);
        Assert.Equal(elv, record.StartedById);
        Assert.Equal(GenerationOutcome.Succeeded, record.Outcome);
        Assert.Equal("keep it short", record.InstructionText);
        Assert.True(record.InputTokens > 0);
        Assert.True(record.OutputTokens > 0);
        // An LLM call has no billed pages — null, not zero, so it can never be mistaken for a
        // Content Understanding call that came back without its quantities.
        Assert.Null(record.Pages);

        // Priced from the fake-model rates in TestServices.Pricing.
        Assert.True(record.EstimatedCostUsd > 0m);

        // The payload is what makes a call reconstructable later.
        Assert.Contains("[system]", record.RequestText);
        Assert.Contains("Client tender material.", record.RequestText);
        Assert.Equal(result.Text, record.ResponseText!.Trim());
    }

    [Fact]
    public async Task Content_generation_writes_one_row_per_unit_sharing_the_operation()
    {
        var (elv, proposalId) = await SetupAsync();
        var generation = TestServices.Generation(db);

        var structure = new StructurePayload
        {
            Items =
            [
                new StructureItem { Title = "Opening", KeyMessage = "Why us" },
                new StructureItem { Title = "Approach", KeyMessage = "How" },
                new StructureItem { Title = "Price", KeyMessage = "What it costs" },
            ],
        };
        await generation.ApplyAsync(proposalId, ArtifactType.Structure, elv, null, structure.ToJson());

        var (operationId, payload) = await TestServices.ContentGeneration(db)
            .GenerateAllAsync(proposalId, elv, null);

        Assert.Equal(3, payload.Units.Count);

        await using var check = db.CreateDbContext();
        var records = check.AiUsage.Where(r => r.OperationId == operationId).ToList();

        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(AiOperation.GenerateContentUnit, r.Operation));
        Assert.Equal(
            ["Approach", "Opening", "Price"],
            records.Select(r => r.Label).Order().ToArray());
    }

    [Fact]
    public async Task Rejecting_a_generation_marks_every_call_of_the_operation()
    {
        var (elv, proposalId) = await SetupAsync();
        var generation = TestServices.Generation(db);

        var structure = new StructurePayload
        {
            Items =
            [
                new StructureItem { Title = "Opening", KeyMessage = "Why us" },
                new StructureItem { Title = "Approach", KeyMessage = "How" },
            ],
        };
        await generation.ApplyAsync(proposalId, ArtifactType.Structure, elv, null, structure.ToJson());
        var (operationId, _) = await TestServices.ContentGeneration(db)
            .GenerateAllAsync(proposalId, elv, null);

        await generation.MarkRejectedAsync(operationId);

        await using var check = db.CreateDbContext();
        var records = check.AiUsage.Where(r => r.OperationId == operationId).ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(GenerationOutcome.Rejected, r.Outcome));
        // The money was spent regardless, so a rejected call still carries its cost.
        Assert.All(records, r => Assert.True(r.EstimatedCostUsd > 0m));
    }

    [Fact]
    public async Task Condensation_is_metered_and_attributed_to_the_user()
    {
        // Roughly 1650 estimated tokens of material against a budget of 100 forces condensation.
        var (elv, proposalId) = await SetupAsync(string.Join(" ", Enumerable.Repeat("tender", 1200)));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:ContextTokenBudget"] = "100",
        }).Build();
        var contextService = TestServices.WorkingContext(db, TestServices.Ai(db), config);

        var loaded = await contextService.LoadAsync(proposalId, elv);
        Assert.True(loaded.UseCondensed);

        await using var check = db.CreateDbContext();
        var records = check.AiUsage
            .Where(r => r.ProposalId == proposalId && r.Operation == AiOperation.CondenseDocument)
            .ToList();

        // Previously this path made AI calls that were never recorded at all.
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Equal(elv, r.StartedById));
        Assert.All(records, r => Assert.Equal("tender.pdf", r.Label));
        Assert.All(records, r => Assert.Equal(GenerationOutcome.Succeeded, r.Outcome));
    }

    [Fact]
    public async Task Document_extraction_is_recorded_as_content_understanding_with_pages()
    {
        var (elv, proposalId) = await SetupAsync(material: null);
        var extractor = TestServices.Extractor(db, new FakeDocumentExtractorStub());
        var documents = new DocumentService(db, new TempDirStorage(_storageDir), extractor);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        await documents.UploadAsync(proposalId, elv, "tender.pdf", stream);

        await using var check = db.CreateDbContext();
        var record = check.AiUsage.Single(r => r.ProposalId == proposalId);

        Assert.Equal(AiServiceKind.ContentUnderstanding, record.Service);
        Assert.Equal(AiOperation.ExtractDocument, record.Operation);
        Assert.Equal("prebuilt-layout", record.Model);
        Assert.Equal("tender.pdf", record.Label);
        // The stub reports what a scanned PDF bills: 4 pages on the Standard meter.
        Assert.Equal(4, record.StandardPages);
        Assert.Equal(0, record.MinimalPages);
        Assert.Equal(4, record.Pages);
        Assert.Equal(0, record.InputTokens);
        // 4 standard pages at 5.00 USD / 1000 pages.
        Assert.Equal(0.02m, record.EstimatedCostUsd);
        Assert.Contains("tender.pdf", record.RequestText);
    }

    /// <summary>
    /// The bug this whole split exists for: page <em>geometry</em> was used as the billing unit, and
    /// Office files come back with none, so every Office upload recorded a plausible-looking 0.00
    /// while Azure charged for it. An unreported quantity must now read as unknown — null, not zero —
    /// so it can never again be mistaken for a free call.
    /// </summary>
    [Fact]
    public async Task An_extraction_that_reports_no_quantities_is_recorded_as_unknown_not_free()
    {
        var (elv, proposalId) = await SetupAsync(material: null);
        var extractor = TestServices.Extractor(db, new SilentExtractorStub());
        var documents = new DocumentService(db, new TempDirStorage(_storageDir), extractor);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        await documents.UploadAsync(proposalId, elv, "tender.docx", stream);

        await using var check = db.CreateDbContext();
        var record = check.AiUsage.Single(r => r.ProposalId == proposalId);

        Assert.Equal(GenerationOutcome.Succeeded, record.Outcome);
        Assert.Null(record.MinimalPages);
        Assert.Null(record.StandardPages);
        Assert.Null(record.Pages);
        Assert.Equal(0m, record.EstimatedCostUsd);
    }

    [Fact]
    public async Task A_failed_call_is_still_recorded()
    {
        var (elv, proposalId) = await SetupAsync(material: null);
        var extractor = TestServices.Extractor(db, new ThrowingExtractorStub());
        var documents = new DocumentService(db, new TempDirStorage(_storageDir), extractor);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => documents.UploadAsync(proposalId, elv, "tender.pdf", stream));

        await using var check = db.CreateDbContext();
        var record = check.AiUsage.Single(r => r.ProposalId == proposalId);
        Assert.Equal(GenerationOutcome.Failed, record.Outcome);
        Assert.Equal("extraction exploded", record.ErrorMessage);
        Assert.Equal(0m, record.EstimatedCostUsd);
    }

    [Fact]
    public async Task Proposal_usage_groups_by_service_and_model()
    {
        var (elv, proposalId) = await SetupAsync();

        await TestServices.Generation(db)
            .GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);

        var extractor = TestServices.Extractor(db, new FakeDocumentExtractorStub());
        var documents = new DocumentService(db, new TempDirStorage(_storageDir), extractor);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("tender content"));
        await documents.UploadAsync(proposalId, elv, "extra.pdf", stream);

        var usage = await TestServices.Usage(db).GetProposalUsageAsync(proposalId, elv);

        Assert.Equal(2, usage.Totals.Calls);
        Assert.Equal(4, usage.Totals.Pages);
        Assert.Equal(2, usage.Breakdown.Count);
        Assert.Contains(usage.Breakdown, b => b.Service == AiServiceKind.AzureOpenAI && b.Model == "fake-model");
        Assert.Contains(usage.Breakdown, b => b.Service == AiServiceKind.ContentUnderstanding
                                              && b.Model == "prebuilt-layout");
        Assert.Equal(usage.Totals.CostUsd, usage.Breakdown.Sum(b => b.Totals.CostUsd));
    }

    [Fact]
    public async Task Usage_is_only_readable_by_someone_with_access()
    {
        var (elv, proposalId) = await SetupAsync();
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;

        await TestServices.Generation(db)
            .GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => TestServices.Usage(db).GetProposalUsageAsync(proposalId, sda));
    }

    /// <summary>Reports billed quantities the way the real extractor reads them off Azure.</summary>
    private sealed class FakeDocumentExtractorStub : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => Task.FromResult(new ExtractionResult("extracted markdown", [new PageSpan(1, 0, 10)],
                new ExtractionUsage(MinimalPages: 0, BasicPages: 0, StandardPages: 4)));
    }

    /// <summary>Succeeds but reports no usage — what an Office upload looked like before the fix.</summary>
    private sealed class SilentExtractorStub : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".docx" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => Task.FromResult(new ExtractionResult("extracted markdown", [new PageSpan(1, 0, 10)]));
    }

    private sealed class ThrowingExtractorStub : IDocumentTextExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".pdf" };

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
            AiCallContext? context = null, CancellationToken ct = default)
            => throw new InvalidOperationException("extraction exploded");
    }
}
