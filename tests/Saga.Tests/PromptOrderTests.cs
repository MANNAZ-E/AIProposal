using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>Records every request it receives, then answers like the fake.</summary>
file sealed class CapturingAiService : IAiService
{
    public List<AiRequest> Requests { get; } = [];
    private readonly FakeAiService _inner = new();

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return _inner.StreamAsync(request, ct);
    }
}

/// <summary>
/// Every paid call is assembled system prompt → material → instruction, so the expensive part (the
/// client material) sits at a byte-identical offset from one call to the next and the provider bills
/// it as cached input. These tests pin that order down — moving steering back in front of the
/// material still produces correct output, which is exactly why the regression would go unnoticed.
/// </summary>
public class PromptOrderTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private async Task<(Guid ElvId, Guid ProposalId)> SetupAsync(string title)
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, title, "C", null, OutputFormat.PowerPoint);
        await using var setup = db.CreateDbContext();
        setup.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            DocumentTypeId = await TestServices.DefaultDocumentTypeAsync(db, proposalId),
            Kind = DocumentKind.Upload,
            Name = "tender.pdf",
            ExtractedText = "TENDER-TEXT: the client material.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await setup.SaveChangesAsync();
        return (elv, proposalId);
    }

    [Fact]
    public async Task Steering_a_regeneration_leaves_the_material_prefix_untouched()
    {
        var (elv, proposalId) = await SetupAsync("Steered");
        var capturing = new CapturingAiService();
        var generation = TestServices.Generation(db, TestServices.Ai(db, capturing));

        await generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);
        await generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, "Focus on the risks.", null);

        var first = capturing.Requests[0];
        var second = capturing.Requests[1];
        Assert.Equal(first.SystemPrompt, second.SystemPrompt);
        Assert.Equal(first.Messages[0].Content, second.Messages[0].Content);
        Assert.Contains("TENDER-TEXT", second.Messages[0].Content);

        // The steering rides in the trailing message, behind everything that was already cached.
        Assert.DoesNotContain("Focus on the risks.", first.SystemPrompt + first.Messages[0].Content);
        Assert.DoesNotContain("Focus on the risks.", second.SystemPrompt + second.Messages[0].Content);
        Assert.Contains("Focus on the risks.", second.Messages[^1].Content);
    }

    [Fact]
    public async Task Every_content_unit_replays_the_same_prefix()
    {
        var (elv, proposalId) = await SetupAsync("Units");
        var capturing = new CapturingAiService();
        var metered = TestServices.Ai(db, capturing);
        var structure = new StructurePayload
        {
            Items =
            [
                new StructureItem { Title = "Slide one", KeyMessage = "Message one" },
                new StructureItem { Title = "Slide two", KeyMessage = "Message two" },
                new StructureItem { Title = "Slide three", KeyMessage = "Message three" },
            ],
        };
        await TestServices.Generation(db, metered)
            .ApplyAsync(proposalId, ArtifactType.Structure, elv, null, structure.ToJson());

        await TestServices.ContentGeneration(db, metered).GenerateAllAsync(proposalId, elv, null);

        Assert.Equal(structure.Items.Count, capturing.Requests.Count);
        var first = capturing.Requests[0];
        foreach (var request in capturing.Requests)
        {
            Assert.Equal(first.SystemPrompt, request.SystemPrompt);
            Assert.Equal(first.Messages[0].Content, request.Messages[0].Content);
        }

        // Only the tail differs, and it is the tail that names the slide.
        Assert.Equal(capturing.Requests.Count,
            capturing.Requests.Select(r => r.Messages[^1].Content).Distinct().Count());
        Assert.Contains("Slide two", capturing.Requests[1].Messages[^1].Content);
        Assert.DoesNotContain("Slide two", capturing.Requests[0].Messages[^1].Content);
    }

    [Fact]
    public async Task Requirements_extraction_reuses_one_system_prompt_for_every_chunk()
    {
        var (elv, proposalId) = await SetupAsync("Chunks");
        var capturing = new CapturingAiService();
        var extraction = new RequirementsExtractionService(db, TestServices.Ai(db, capturing));

        await using (var setup = db.CreateDbContext())
        {
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                DocumentTypeId = await TestServices.DefaultDocumentTypeAsync(db, proposalId),
                Kind = DocumentKind.Upload,
                Name = "annex.pdf",
                ExtractedText = "ANNEX-TEXT: further requirements.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        await extraction.ExtractAsync(proposalId, elv);

        Assert.Equal(2, capturing.Requests.Count);
        // The document a chunk came from travels with the chunk, not in the prefix.
        Assert.Equal(capturing.Requests[0].SystemPrompt, capturing.Requests[1].SystemPrompt);
        Assert.DoesNotContain("annex.pdf", capturing.Requests[0].SystemPrompt);
        Assert.Contains("annex.pdf", capturing.Requests[1].Messages[0].Content);
        Assert.Contains("ANNEX-TEXT", capturing.Requests[1].Messages[0].Content);
    }
}
