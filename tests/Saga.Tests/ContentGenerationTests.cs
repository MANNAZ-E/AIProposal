using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ContentGenerationTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ArtifactService _artifacts = new(db);
    private readonly GenerationService _generation = TestServices.Generation(db);
    private readonly ContentGenerationService _content = TestServices.ContentGeneration(db);

    private async Task<(Guid ElvId, Guid ProposalId, StructurePayload Structure)> SetupWithStructureAsync()
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
                ExtractedText = "Client material.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var structure = new StructurePayload
        {
            Items =
            [
                new StructureItem { Title = "Slide one", KeyMessage = "Message one" },
                new StructureItem { Title = "Slide two", KeyMessage = "Message two" },
            ],
        };
        await _generation.ApplyAsync(proposalId, ArtifactType.Structure, elv, null, structure.ToJson());
        return (elv, proposalId, structure);
    }

    [Fact]
    public async Task Content_requires_a_structure()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "NoStructure", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.GenerateAllAsync(proposalId, elv, null));
    }

    [Fact]
    public async Task Generates_one_unit_per_structure_item_with_progress()
    {
        var (elv, proposalId, structure) = await SetupWithStructureAsync();

        var progress = new List<ContentProgress>();
        var (_, payload) = await _content.GenerateAllAsync(proposalId, elv, null,
            p => { progress.Add(p); return Task.CompletedTask; });

        Assert.Equal(2, payload.Units.Count);
        Assert.Equal(structure.Items[0].Id, payload.Units[0].StructureItemId);
        Assert.Equal("Slide one", payload.Units[0].Title);
        Assert.NotEmpty(payload.Units[0].BodyMarkdown);
        Assert.Equal(2, progress.Count);
        Assert.Equal("Slide two", progress[1].Title);
    }

    [Fact]
    public async Task Locked_units_survive_regenerate_all()
    {
        var (elv, proposalId, _) = await SetupWithStructureAsync();
        var (_, first) = await _content.GenerateAllAsync(proposalId, elv, null);
        first.Units[0].IsLocked = true;
        first.Units[0].BodyMarkdown = "HAND-POLISHED CONTENT";
        await _generation.ApplyAsync(proposalId, ArtifactType.Content, elv, null, first.ToJson());

        var (_, second) = await _content.GenerateAllAsync(proposalId, elv, null);

        Assert.Equal("HAND-POLISHED CONTENT", second.Units[0].BodyMarkdown);
        Assert.True(second.Units[0].IsLocked);
        Assert.NotEqual("HAND-POLISHED CONTENT", second.Units[1].BodyMarkdown);
    }

    [Fact]
    public async Task Single_unit_regeneration_is_staged_and_locked_units_refuse()
    {
        var (elv, proposalId, _) = await SetupWithStructureAsync();
        var (_, payload) = await _content.GenerateAllAsync(proposalId, elv, null);
        await _generation.ApplyAsync(proposalId, ArtifactType.Content, elv, null, payload.ToJson());

        var unit = payload.Units[0];
        var (_, body) = await _content.RegenerateUnitAsync(proposalId, unit.Id, elv, "shorter");
        Assert.NotEmpty(body);

        // Staging: the stored artifact is unchanged until the user accepts.
        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Content, elv);
        var stored = ContentPayload.FromJson(artifact!.ContentJson);
        Assert.Equal(unit.BodyMarkdown, stored.Units[0].BodyMarkdown);

        // Locked unit refuses regeneration.
        stored.Units[1].IsLocked = true;
        await _generation.ApplyAsync(proposalId, ArtifactType.Content, elv, null, stored.ToJson());
        var lockedId = stored.Units[1].Id;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.RegenerateUnitAsync(proposalId, lockedId, elv, null));
    }
}
