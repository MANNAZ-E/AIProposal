using Microsoft.Extensions.Configuration;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ArtifactLifecycleTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ArtifactService _artifacts = new(db);
    private readonly GenerationService _generation = new(db, new FakeAiService(),
        new ConfigurationBuilder().Build());

    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId)> SetupWithMaterialAsync()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
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
        return (elv, sda, proposalId);
    }

    [Fact]
    public async Task Generate_apply_creates_artifact_with_version_and_run_log()
    {
        var (elv, _, proposalId) = await SetupWithMaterialAsync();

        var deltas = new List<string>();
        var result = await _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null,
            d => { deltas.Add(d); return Task.CompletedTask; });

        Assert.NotEmpty(deltas);
        Assert.Equal(string.Concat(deltas).Trim(), result.Text);

        await _generation.ApplyAsync(proposalId, ArtifactType.Summary, elv, result.Text, null);

        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Summary, elv);
        Assert.Equal(ArtifactStatus.Generated, artifact!.Status);
        Assert.Equal(result.Text, artifact.ContentMarkdown);
        Assert.False(artifact.IsStale);

        var versions = await _artifacts.GetVersionsAsync(proposalId, ArtifactType.Summary, elv);
        Assert.Single(versions);
        Assert.Equal(VersionOrigin.Generated, versions[0].Origin);

        await using var check = db.CreateDbContext();
        var run = Assert.Single(check.GenerationRuns.Where(r => r.ProposalId == proposalId));
        Assert.Equal(GenerationOutcome.Succeeded, run.Outcome);
        Assert.True(run.PromptTokens > 0);
    }

    [Fact]
    public async Task Generation_without_material_is_refused()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "Empty", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null));
    }

    [Fact]
    public async Task Locked_artifact_cannot_be_generated_edited_or_restored()
    {
        var (elv, _, proposalId) = await SetupWithMaterialAsync();
        var result = await _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);
        await _generation.ApplyAsync(proposalId, ArtifactType.Summary, elv, result.Text, null);
        await _artifacts.SetLockedAsync(proposalId, ArtifactType.Summary, elv, locked: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _generation.ApplyAsync(proposalId, ArtifactType.Summary, elv, "overwrite", null));

        var versions = await _artifacts.GetVersionsAsync(proposalId, ArtifactType.Summary, elv);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _artifacts.RestoreVersionAsync(versions[0].Id, elv));
    }

    [Fact]
    public async Task Concurrent_edit_is_detected_and_carries_the_other_version()
    {
        var (elv, sda, proposalId) = await SetupWithMaterialAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var result = await _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);
        await _generation.ApplyAsync(proposalId, ArtifactType.Summary, elv, result.Text, null);

        // Both load the artifact, sda saves first, elv's save must conflict.
        var elvCopy = await _artifacts.GetAsync(proposalId, ArtifactType.Summary, elv);
        await _artifacts.SaveEditAsync(proposalId, ArtifactType.Summary, sda, "sda's edit", null, elvCopy!.RowVersion);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _artifacts.SaveEditAsync(proposalId, ArtifactType.Summary, elv, "elv's edit", null, elvCopy.RowVersion));
        Assert.Equal("sda's edit", conflict.CurrentMarkdown);

        // Saving again with the fresh row version succeeds ("save my version anyway").
        await _artifacts.SaveEditAsync(proposalId, ArtifactType.Summary, elv, "elv's edit", null, conflict.CurrentRowVersion);
        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Summary, elv);
        Assert.Equal("elv's edit", artifact!.ContentMarkdown);
    }

    [Fact]
    public async Task Edit_marks_downstream_stale_and_restore_recovers_old_content()
    {
        var (elv, _, proposalId) = await SetupWithMaterialAsync();
        var result = await _generation.GenerateAsync(proposalId, ArtifactType.Summary, elv, null, null);
        await _generation.ApplyAsync(proposalId, ArtifactType.Summary, elv, result.Text, null);

        // A downstream artifact exists.
        await _generation.ApplyAsync(proposalId, ArtifactType.Scoping, elv, "scoping content", null);

        var artifact = await _artifacts.GetAsync(proposalId, ArtifactType.Summary, elv);
        await _artifacts.SaveEditAsync(proposalId, ArtifactType.Summary, elv, "edited summary", null, artifact!.RowVersion);

        var scoping = await _artifacts.GetAsync(proposalId, ArtifactType.Scoping, elv);
        Assert.True(scoping!.IsStale);

        // Restore the original generated version.
        var versions = await _artifacts.GetVersionsAsync(proposalId, ArtifactType.Summary, elv);
        var generated = versions.Single(v => v.Origin == VersionOrigin.Generated);
        await _artifacts.RestoreVersionAsync(generated.Id, elv);

        var restored = await _artifacts.GetAsync(proposalId, ArtifactType.Summary, elv);
        Assert.Equal(result.Text, restored!.ContentMarkdown);
        Assert.Equal(3, (await _artifacts.GetVersionsAsync(proposalId, ArtifactType.Summary, elv)).Count);
    }

    [Fact]
    public async Task Reader_cannot_generate_or_edit()
    {
        var (elv, sda, proposalId) = await SetupWithMaterialAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _generation.GenerateAsync(proposalId, ArtifactType.Summary, sda, null, null));
    }
}
