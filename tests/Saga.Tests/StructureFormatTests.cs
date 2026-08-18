using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>
/// The output format drives what the structure means: PowerPoint counts slides per entry,
/// Word counts words per section. Switching format keeps the shared columns.
/// </summary>
public class StructureFormatTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ArtifactService _artifacts = new(db);
    private readonly GenerationService _generation = TestServices.Generation(db);

    private async Task<(Guid ElvId, Guid ProposalId)> SetupAsync(OutputFormat format)
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, format);
        await using var setup = db.CreateDbContext();
        setup.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Kind = DocumentKind.Upload,
            Name = "tender.pdf",
            ExtractedText = "The client requests a leadership development program.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await setup.SaveChangesAsync();
        return (elv, proposalId);
    }

    private async Task<List<StructureItem>> GenerateStructureAsync(Guid proposalId, Guid userId)
    {
        var result = await _generation.GenerateAsync(proposalId, ArtifactType.Structure, userId, null, null);
        return ModelJson.ParseArray<StructureItem>(result.Text);
    }

    [Fact]
    public async Task PowerPoint_structure_is_measured_in_slides()
    {
        var (elv, proposalId) = await SetupAsync(OutputFormat.PowerPoint);

        var items = await GenerateStructureAsync(proposalId, elv);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.NotNull(i.SlideCount));
        Assert.All(items, i => Assert.Null(i.WordCount));
    }

    [Fact]
    public async Task Word_structure_is_measured_in_words()
    {
        var (elv, proposalId) = await SetupAsync(OutputFormat.Word);

        var items = await GenerateStructureAsync(proposalId, elv);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.NotNull(i.WordCount));
        Assert.All(items, i => Assert.Null(i.SlideCount));
    }

    [Fact]
    public async Task Switching_format_keeps_title_purpose_and_key_message()
    {
        var (elv, proposalId) = await SetupAsync(OutputFormat.PowerPoint);
        var payload = new StructurePayload
        {
            Items =
            [
                new StructureItem
                {
                    Title = "Understanding your situation",
                    Purpose = "Show we understand the context",
                    KeyMessage = "We know where you are",
                    SlideCount = 2,
                    WordCount = 350,
                },
            ],
        };
        await _generation.ApplyAsync(proposalId, ArtifactType.Structure, elv, null, payload.ToJson());

        await _proposals.SetOutputFormatAsync(proposalId, elv, OutputFormat.Word);

        var (proposal, _) = (await _proposals.GetForUserAsync(proposalId, elv))!.Value;
        Assert.Equal(OutputFormat.Word, proposal.OutputFormat);

        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Structure, elv);
        var item = Assert.Single(StructurePayload.FromJson(artifact!.ContentJson).Items);
        Assert.Equal("Understanding your situation", item.Title);
        Assert.Equal("Show we understand the context", item.Purpose);
        Assert.Equal("We know where you are", item.KeyMessage);
        // Both lengths survive the switch, so switching back does not lose the slide count.
        Assert.Equal(2, item.SlideCount);
        Assert.Equal(350, item.WordCount);
    }
}
