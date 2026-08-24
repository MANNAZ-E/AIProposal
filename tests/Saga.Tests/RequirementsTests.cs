using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class RequirementsTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ArtifactService _artifacts = new(db);

    [Fact]
    public void Chunker_respects_page_boundaries_and_size()
    {
        var text = string.Concat(Enumerable.Range(1, 10).Select(p => new string((char)('a' + p - 1), 1000)));
        var pages = Enumerable.Range(1, 10).Select(p => new PageSpan(p, (p - 1) * 1000, 1000)).ToList();

        var chunks = DocumentChunker.Chunk(text, pages, maxChars: 3500);

        Assert.Equal(4, chunks.Count);
        Assert.Equal("pages 1–3", chunks[0].LocationLabel);
        Assert.Equal(3000, chunks[0].Text.Length);
        Assert.Equal("page 10", chunks[3].LocationLabel);
        // No text is lost.
        Assert.Equal(text.Length, chunks.Sum(c => c.Text.Length));
    }

    [Fact]
    public void Chunker_without_page_map_splits_by_length()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"Paragraph {i} with some text."));
        var chunks = DocumentChunker.Chunk(text, null, maxChars: 300);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 300));
        Assert.Equal(text.Length, chunks.Sum(c => c.Text.Length));
    }

    [Fact]
    public void Parse_tolerates_markdown_fences_and_prose()
    {
        var output = """
            Here are the requirements:
            ```json
            [{"text": "Must deliver by June", "type": "Mandatory", "interpretation": "deadline", "howAddressed": "plan"}]
            ```
            """;

        var items = RequirementsExtractionService.ParseItems(output);
        var item = Assert.Single(items);
        Assert.Equal("Must deliver by June", item.Text);
        Assert.Equal(RequirementType.Mandatory, item.Type);
    }

    [Fact]
    public void Parse_returns_empty_for_garbage()
    {
        Assert.Empty(RequirementsExtractionService.ParseItems("no json here"));
        Assert.Empty(RequirementsExtractionService.ParseItems("[not valid json]"));
    }

    [Fact]
    public void Deduplicate_removes_near_identical_items()
    {
        var payload = new RequirementsPayload
        {
            Items =
            [
                new RequirementItem { Text = "The supplier must document experience." },
                new RequirementItem { Text = "the supplier must document experience" },
                new RequirementItem { Text = "A different requirement." },
            ],
        };

        RequirementsExtractionService.Deduplicate(payload);

        Assert.Equal(2, payload.Items.Count);
    }

    [Fact]
    public async Task Extraction_produces_sourced_items_and_logs_a_run()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await using (var setup = db.CreateDbContext())
        {
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Kind = DocumentKind.Upload,
                Name = "tender.pdf",
                ExtractedText = "The offer must be submitted on time. Suppliers must document experience.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var extraction = new RequirementsExtractionService(db, TestServices.Ai(db));
        var progress = new List<ExtractionProgress>();
        var (operationId, payload) = await extraction.ExtractAsync(proposalId, elv,
            p => { progress.Add(p); return Task.CompletedTask; });

        Assert.NotEmpty(payload.Items);
        Assert.All(payload.Items, i => Assert.Equal("tender.pdf", i.SourceDocument));
        Assert.NotEmpty(progress);

        await using var check = db.CreateDbContext();
        // One call per chunk, all sharing the operation id.
        var runs = check.AiUsage.Where(r => r.OperationId == operationId).ToList();
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(GenerationOutcome.Succeeded, r.Outcome));
        Assert.All(runs, r => Assert.Equal(ArtifactType.Requirements, r.ArtifactType));
        Assert.All(runs, r => Assert.Equal(AiOperation.ExtractRequirements, r.Operation));

        // Round-trip through the artifact JSON payload.
        var generation = TestServices.Generation(db);
        await generation.ApplyAsync(proposalId, ArtifactType.Requirements, elv, null, payload.ToJson());
        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Requirements, elv);
        var restored = RequirementsPayload.FromJson(artifact!.ContentJson);
        Assert.Equal(payload.Items.Count, restored.Items.Count);
    }
}
