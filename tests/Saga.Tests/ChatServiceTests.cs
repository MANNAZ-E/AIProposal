using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
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

public class ChatServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private ChatService NewChat(IAiService ai)
        => new(db, ai, TestServices.WorkingContext(db), new ConfigurationBuilder().Build());

    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId)> SetupAsync()
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
            ExtractedText = "TENDER-TEXT: the deadline is 15 August.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        setup.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Type = ArtifactType.Summary,
            Status = ArtifactStatus.Generated,
            ContentMarkdown = "SUMMARY-TEXT",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await setup.SaveChangesAsync();
        return (elv, sda, proposalId);
    }

    [Fact]
    public async Task Ask_persists_question_and_answer_and_logs_a_run()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat(new FakeAiService());

        var deltas = new List<string>();
        var answer = await chat.AskAsync(proposalId, elv, "What is the deadline?",
            WorkingContextKind.FullProject, d => { deltas.Add(d); return Task.CompletedTask; });

        Assert.NotEmpty(deltas);
        Assert.Equal(string.Concat(deltas).Trim(), answer.Text);

        var messages = await chat.GetMessagesAsync(proposalId, elv);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("What is the deadline?", messages[0].Text);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(WorkingContextKind.FullProject, messages[1].WorkingContext);

        await using var check = db.CreateDbContext();
        var run = check.GenerationRuns.Single(r => r.ProposalId == proposalId);
        Assert.Null(run.ArtifactType); // Chat runs are not tied to an artifact.
        Assert.Equal(GenerationOutcome.Succeeded, run.Outcome);
        Assert.True(run.CompletionTokens > 0);
    }

    [Fact]
    public async Task Source_material_context_hides_artifacts_from_the_model()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        await chat.AskAsync(proposalId, elv, "Q1", WorkingContextKind.SourceMaterial);
        await chat.AskAsync(proposalId, elv, "Q2", WorkingContextKind.FullProject);

        var sourceOnly = capturing.Requests[0].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", sourceOnly);
        Assert.DoesNotContain("SUMMARY-TEXT", sourceOnly);

        var fullProject = capturing.Requests[1].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", fullProject);
        Assert.Contains("SUMMARY-TEXT", fullProject);
    }

    [Fact]
    public async Task History_travels_with_the_next_question()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        await chat.AskAsync(proposalId, elv, "First question", WorkingContextKind.FullProject);
        await chat.AskAsync(proposalId, elv, "Second question", WorkingContextKind.FullProject);

        var second = capturing.Requests[1].Messages;
        Assert.Contains(second, m => m.Role == "user" && m.Content == "First question");
        Assert.Contains(second, m => m.Role == "assistant" && m.Content.Length > 0);
        Assert.Equal("Second question", second[^1].Content);
    }

    [Fact]
    public async Task Readers_can_chat_but_outsiders_cannot()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var chat = NewChat(new FakeAiService());

        var answer = await chat.AskAsync(proposalId, sda, "May I ask?", WorkingContextKind.SourceMaterial);
        Assert.NotEmpty(answer.Text);

        await Assert.ThrowsAnyAsync<Exception>(
            () => chat.AskAsync(proposalId, Guid.NewGuid(), "Hi", WorkingContextKind.SourceMaterial));
    }
}
