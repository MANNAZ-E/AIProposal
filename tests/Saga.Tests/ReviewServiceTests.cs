using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ReviewServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ReviewService _review = new(db, TestServices.Ai(db),
        TestServices.WorkingContext(db));

    private async Task<(Guid ElvId, Guid ProposalId, RequirementsPayload Requirements)> SetupAsync(
        bool withRequirements = true, bool withContent = true)
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
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
        var content = new ContentPayload
        {
            Units = [new ContentUnit { Title = "Our approach", BodyMarkdown = "We propose..." }],
        };

        await using var setup = db.CreateDbContext();
        setup.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            DocumentTypeId = await TestServices.DefaultDocumentTypeAsync(db, proposalId),
            Kind = DocumentKind.Upload,
            Name = "tender.pdf",
            ExtractedText = "Client material.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (withRequirements)
            setup.Artifacts.Add(NewArtifact(proposalId, ArtifactType.Requirements, requirements.ToJson()));
        if (withContent)
            setup.Artifacts.Add(NewArtifact(proposalId, ArtifactType.Content, content.ToJson()));
        await setup.SaveChangesAsync();
        return (elv, proposalId, requirements);
    }

    private static Artifact NewArtifact(Guid proposalId, ArtifactType type, string json) => new()
    {
        Id = Guid.NewGuid(),
        ProposalId = proposalId,
        Type = type,
        Status = ArtifactStatus.Generated,
        ContentJson = json,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Review_covers_every_requirement_exactly_once_and_logs_a_run()
    {
        var (elv, proposalId, requirements) = await SetupAsync();

        var (operationId, payload) = await _review.GenerateAsync(proposalId, elv);

        Assert.Equal(requirements.Items.Count, payload.Items.Count);
        Assert.Equal(
            requirements.Items.Select(r => r.Id).ToHashSet(),
            payload.Items.Select(i => i.RequirementId).ToHashSet());
        Assert.All(payload.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.RequirementText)));
        // The fake cycles coverages, so all three verdicts appear.
        Assert.Contains(payload.Items, i => i.Coverage == ReviewCoverage.Addressed);
        Assert.Contains(payload.Items, i => i.Coverage == ReviewCoverage.NotAddressed);

        await using var check = db.CreateDbContext();
        var run = check.AiUsage.Single(r => r.OperationId == operationId);
        Assert.Equal(ArtifactType.Review, run.ArtifactType);
        Assert.Equal(AiOperation.ReviewDraft, run.Operation);
        Assert.Equal(GenerationOutcome.Succeeded, run.Outcome);
    }

    [Fact]
    public async Task Review_payload_round_trips_through_artifact_json()
    {
        var (elv, proposalId, _) = await SetupAsync();
        var (_, payload) = await _review.GenerateAsync(proposalId, elv);

        var restored = ReviewPayload.FromJson(payload.ToJson());
        Assert.Equal(payload.Items.Count, restored.Items.Count);
        Assert.Equal(payload.Items[0].Coverage, restored.Items[0].Coverage);
        Assert.Equal(payload.Items[0].RequirementId, restored.Items[0].RequirementId);
    }

    [Fact]
    public async Task Review_requires_requirements_and_content()
    {
        var (elv, withoutRequirements, _) = await SetupAsync(withRequirements: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _review.GenerateAsync(withoutRequirements, elv));

        var (elv2, withoutContent, _) = await SetupAsync(withContent: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _review.GenerateAsync(withoutContent, elv2));
    }
}
