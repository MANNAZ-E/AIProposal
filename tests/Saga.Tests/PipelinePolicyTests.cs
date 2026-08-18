using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>
/// DB-backed tests for the M6 pipeline policies: the output-format setting and the
/// token-budget condensation fallback.
/// </summary>
public class PipelinePolicyTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private async Task<(Guid ElvId, Guid ProposalId)> SetupAsync()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        return (elv, proposalId);
    }

    private async Task AddArtifactAsync(Guid proposalId, ArtifactType type, bool locked = false)
    {
        await using var setup = db.CreateDbContext();
        setup.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Type = type,
            Status = ArtifactStatus.Generated,
            ContentMarkdown = "content",
            IsLocked = locked,
            GeneratedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await setup.SaveChangesAsync();
    }

    [Fact]
    public async Task Output_format_change_keeps_every_artifact_untouched()
    {
        var (elv, proposalId) = await SetupAsync();
        await AddArtifactAsync(proposalId, ArtifactType.Summary);
        await AddArtifactAsync(proposalId, ArtifactType.Structure);
        await AddArtifactAsync(proposalId, ArtifactType.Content, locked: true);

        await _proposals.SetOutputFormatAsync(proposalId, elv, OutputFormat.Word);

        await using var check = db.CreateDbContext();
        var artifacts = await check.Artifacts.Where(a => a.ProposalId == proposalId)
            .ToDictionaryAsync(a => a.Type);
        Assert.All(artifacts.Values, a => Assert.Equal("content", a.ContentMarkdown));
        Assert.True(artifacts[ArtifactType.Content].IsLocked);
        Assert.Equal(OutputFormat.Word, (await check.Proposals.FirstAsync(p => p.Id == proposalId)).OutputFormat);
    }

    [Fact]
    public async Task Oversized_material_is_condensed_once_and_used_for_generation()
    {
        var (_, proposalId) = await SetupAsync();
        await using (var setup = db.CreateDbContext())
        {
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Kind = DocumentKind.Upload,
                Name = "huge.pdf",
                ExtractedText = string.Join(" ", Enumerable.Repeat("The client requires many things.", 200)),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Kind = DocumentKind.Note,
                Name = "note",
                ExtractedText = "A note.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        // ~1650 estimated tokens of material against a budget of 100.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:ContextTokenBudget"] = "100",
        }).Build();
        var contextService = TestServices.WorkingContext(db, config: config);

        var condensedFor = new List<string>();
        var loaded = await contextService.LoadAsync(proposalId, name =>
        {
            condensedFor.Add(name);
            return Task.CompletedTask;
        });

        Assert.True(loaded.UseCondensed);
        Assert.Equal(["huge.pdf"], condensedFor); // Notes are never condensed.

        await using var check = db.CreateDbContext();
        var upload = await check.Documents.FirstAsync(d => d.ProposalId == proposalId && d.Kind == DocumentKind.Upload);
        Assert.False(string.IsNullOrWhiteSpace(upload.CondensedText));
        var note = await check.Documents.FirstAsync(d => d.ProposalId == proposalId && d.Kind == DocumentKind.Note);
        Assert.Null(note.CondensedText);

        // A second load reuses the stored condensed text instead of condensing again.
        condensedFor.Clear();
        var second = await contextService.LoadAsync(proposalId, name =>
        {
            condensedFor.Add(name);
            return Task.CompletedTask;
        });
        Assert.True(second.UseCondensed);
        Assert.Empty(condensedFor);
    }

    [Fact]
    public async Task Small_material_is_never_condensed()
    {
        var (_, proposalId) = await SetupAsync();
        await using (var setup = db.CreateDbContext())
        {
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                Kind = DocumentKind.Upload,
                Name = "small.pdf",
                ExtractedText = "Short client material.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var contextService = TestServices.WorkingContext(db);
        var loaded = await contextService.LoadAsync(proposalId);

        Assert.False(loaded.UseCondensed);
        await using var check = db.CreateDbContext();
        var upload = await check.Documents.FirstAsync(d => d.ProposalId == proposalId);
        Assert.Null(upload.CondensedText);
    }
}
