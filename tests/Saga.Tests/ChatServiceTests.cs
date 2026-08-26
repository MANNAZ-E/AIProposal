using Microsoft.EntityFrameworkCore;
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

public class ChatServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private ChatService NewChat(IAiService ai)
    {
        var metered = TestServices.Ai(db, ai);
        return new(db, metered, TestServices.WorkingContext(db, metered));
    }

    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId)> SetupAsync()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        var documentType = await TestServices.DefaultDocumentTypeAsync(db, proposalId);
        await using var setup = db.CreateDbContext();
        setup.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            DocumentTypeId = documentType,
            Kind = DocumentKind.Upload,
            Name = "tender.pdf",
            ExtractedText = "TENDER-TEXT: the deadline is 15 August.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        setup.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            DocumentTypeId = documentType,
            Kind = DocumentKind.Upload,
            Name = "appendix.pdf",
            ExtractedText = "APPENDIX-TEXT: an annex nobody needs.",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
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
        var (chatId, answer) = await chat.AskAsync(proposalId, null, elv, "What is the deadline?",
            onDelta: d => { deltas.Add(d); return Task.CompletedTask; });

        Assert.NotEmpty(deltas);
        Assert.Equal(string.Concat(deltas).Trim(), answer!.Text);

        var messages = await chat.GetMessagesAsync(chatId, elv);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("What is the deadline?", messages[0].Text);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(WorkingContextKind.FullProject, messages[1].WorkingContext);

        await using var check = db.CreateDbContext();
        var session = check.ChatSessions.Single(s => s.Id == chatId);
        Assert.Equal("What is the deadline", session.Title); // Trailing punctuation is trimmed.
        Assert.Equal(elv, session.OwnerId);
        Assert.Equal(ChatVisibility.Private, session.Visibility);
        Assert.NotEqual("", session.ContextSnapshot);
        Assert.Equal(answer.CreatedAt, session.LastMessageAt);

        var run = check.AiUsage.Single(r => r.ProposalId == proposalId);
        Assert.Null(run.ArtifactType); // Chat calls are not tied to an artifact.
        Assert.Equal(AiOperation.Chat, run.Operation);
        Assert.Equal(GenerationOutcome.Succeeded, run.Outcome);
        Assert.Equal(session.Title, run.Label); // The usage page names the chat.
        Assert.True(run.OutputTokens > 0);
    }

    [Fact]
    public async Task Source_material_context_hides_artifacts_from_the_model()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        await chat.AskAsync(proposalId, null, elv, "Q1", WorkingContextKind.SourceMaterial);
        await chat.AskAsync(proposalId, null, elv, "Q2", WorkingContextKind.FullProject);

        var sourceOnly = capturing.Requests[0].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", sourceOnly);
        Assert.DoesNotContain("SUMMARY-TEXT", sourceOnly);

        var fullProject = capturing.Requests[1].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", fullProject);
        Assert.Contains("SUMMARY-TEXT", fullProject);

        // Each null chat id starts a separate chat.
        Assert.Equal(2, (await chat.ListAsync(proposalId, elv)).Count);
    }

    [Fact]
    public async Task History_travels_with_the_next_question()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "First question");
        await chat.AskAsync(proposalId, chatId, elv, "Second question");

        var second = capturing.Requests[1].Messages;
        Assert.Contains(second, m => m.Role == "user" && m.Content == "First question");
        Assert.Contains(second, m => m.Role == "assistant" && m.Content.Length > 0);
        Assert.EndsWith("Second question", second[^1].Content);
    }

    [Fact]
    public async Task History_does_not_leak_between_chats()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        await chat.AskAsync(proposalId, null, elv, "Question in the first chat");
        await chat.AskAsync(proposalId, null, elv, "Question in the second chat");

        var second = capturing.Requests[1].Messages;
        Assert.DoesNotContain(second, m => m.Content.Contains("Question in the first chat"));
    }

    [Fact]
    public async Task Unchecked_documents_never_reach_the_model()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (documents, artifacts) = await chat.GetMaterialAsync(proposalId, elv);
        var tenderOnly = new MaterialSelection(
            [documents.Single(d => d.Name == "tender.pdf").Id],
            artifacts.Select(a => a.Type).ToList());

        await chat.AskAsync(proposalId, null, elv, "Q", WorkingContextKind.FullProject, tenderOnly);

        var context = capturing.Requests[0].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", context);
        Assert.DoesNotContain("APPENDIX-TEXT", context);
    }

    [Fact]
    public async Task An_artifact_emptied_by_editing_drops_out_of_the_material_picker()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var artifacts = new ArtifactService(db);

        var summary = (await artifacts.GetAllAsync(proposalId, elv)).Single(a => a.Type == ArtifactType.Summary);
        await artifacts.SaveEditAsync(proposalId, ArtifactType.Summary, elv, "", null, summary.RowVersion);

        var chat = NewChat(new FakeAiService());
        var (_, materialArtifacts) = await chat.GetMaterialAsync(proposalId, elv);
        Assert.DoesNotContain(materialArtifacts, a => a.Type == ArtifactType.Summary);
    }

    [Fact]
    public async Task Unchecked_artifacts_never_reach_the_model()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (documents, _) = await chat.GetMaterialAsync(proposalId, elv);
        var noArtifacts = new MaterialSelection(documents.Select(d => d.Id).ToList(), []);

        await chat.AskAsync(proposalId, null, elv, "Q", WorkingContextKind.FullProject, noArtifacts);

        var context = capturing.Requests[0].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", context);
        Assert.DoesNotContain("SUMMARY-TEXT", context);
    }

    [Fact]
    public async Task The_client_material_preset_picks_only_what_the_client_supplied()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        await using (var setup = db.CreateDbContext())
        {
            var mannaz = await setup.DocumentTypes
                .SingleAsync(t => t.ProposalId == proposalId && t.Name == DocumentType.MannazMaterialName);
            setup.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                DocumentTypeId = mannaz.Id,
                Kind = DocumentKind.Note,
                Name = "our-approach.md",
                ExtractedText = "MANNAZ-TEXT: how we usually run this.",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(2),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var (documents, artifacts) = await chat.GetMaterialAsync(proposalId, elv);
        var clientOnly = MaterialSelection.ForPreset(WorkingContextKind.ClientMaterial, documents, artifacts);

        await chat.AskAsync(proposalId, null, elv, "Q", WorkingContextKind.ClientMaterial, clientOnly);

        var context = capturing.Requests[0].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", context);
        Assert.DoesNotContain("MANNAZ-TEXT", context);
        Assert.DoesNotContain("SUMMARY-TEXT", context);
    }

    [Fact]
    public async Task Material_is_frozen_after_the_first_question()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "First");

        await using (var edit = db.CreateDbContext())
        {
            var tender = edit.Documents.Single(d => d.ProposalId == proposalId && d.Name == "tender.pdf");
            tender.ExtractedText = "REWRITTEN-TEXT: everything changed.";
            await edit.SaveChangesAsync();
        }

        await chat.AskAsync(proposalId, chatId, elv, "Second");

        // The whole point of freezing: the prefix is identical, so the provider can cache it.
        var context = capturing.Requests[1].Messages[0].Content;
        Assert.Contains("TENDER-TEXT", context);
        Assert.DoesNotContain("REWRITTEN-TEXT", context);
        Assert.Equal(capturing.Requests[0].Messages[0].Content, context);
        Assert.Equal(capturing.Requests[0].SystemPrompt, capturing.Requests[1].SystemPrompt);
    }

    [Fact]
    public async Task Renaming_the_proposal_mid_chat_leaves_the_prefix_untouched()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "First");
        await _proposals.UpdateDetailsAsync(proposalId, elv, "Renamed", "New Client");
        await chat.AskAsync(proposalId, chatId, elv, "Second");

        // The proposal's identity rides with the question, not the cached prefix, so a rename
        // between messages does not shift a single byte the provider already cached.
        Assert.Equal(capturing.Requests[0].SystemPrompt, capturing.Requests[1].SystemPrompt);
        Assert.Equal(capturing.Requests[0].Messages[0].Content, capturing.Requests[1].Messages[0].Content);
        Assert.DoesNotContain("Renamed", capturing.Requests[0].SystemPrompt);
        Assert.DoesNotContain("Renamed", capturing.Requests[1].SystemPrompt);
        Assert.Contains("Renamed", capturing.Requests[1].Messages[^1].Content);
    }

    [Fact]
    public async Task Asking_with_nothing_selected_sends_no_context()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (_, answer) = await chat.AskAsync(proposalId, null, elv, "Q",
            WorkingContextKind.Custom, MaterialSelection.Empty);

        Assert.NotEmpty(answer!.Text);
        // The question is the whole conversation: no context block, so nothing tells the model
        // to answer strictly from material it was never given.
        var request = Assert.Single(capturing.Requests);
        Assert.EndsWith("Q", Assert.Single(request.Messages).Content);
        Assert.DoesNotContain("TENDER-TEXT", request.SystemPrompt);
        Assert.DoesNotContain("Answer strictly from the provided context", request.SystemPrompt);
    }

    [Fact]
    public async Task A_chat_started_with_nothing_selected_stays_empty_on_follow_ups()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var capturing = new CapturingAiService();
        var chat = NewChat(capturing);

        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "First",
            WorkingContextKind.Custom, MaterialSelection.Empty);
        await chat.AskAsync(proposalId, chatId, elv, "Second");

        // An empty selection is frozen like any other: the follow-up must not quietly pick the
        // proposal's material up instead.
        Assert.DoesNotContain("TENDER-TEXT", capturing.Requests[1].Messages[0].Content);
        Assert.Equal(3, capturing.Requests[1].Messages.Count);
    }

    [Fact]
    public async Task Readers_can_chat_in_their_own_private_chat()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var chat = NewChat(new FakeAiService());

        var (chatId, answer) = await chat.AskAsync(proposalId, null, sda, "May I ask?");

        Assert.NotEmpty(answer!.Text);
        await using var check = db.CreateDbContext();
        Assert.Equal(ChatVisibility.Private, check.ChatSessions.Single(s => s.Id == chatId).Visibility);
    }

    [Fact]
    public async Task Readers_cannot_post_in_a_shared_chat_or_share_their_own()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var chat = NewChat(new FakeAiService());

        var (elvChat, _) = await chat.AskAsync(proposalId, null, elv, "Elv asks");
        await chat.SetVisibilityAsync(elvChat, elv, ChatVisibility.Shared);

        // A Reader may read it...
        Assert.Equal(2, (await chat.GetMessagesAsync(elvChat, sda)).Count);
        // ...but not post into it.
        var posting = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.AskAsync(proposalId, elvChat, sda, "Reader tries"));
        Assert.Contains("read-only", posting.Message);

        var (sdaChat, _) = await chat.AskAsync(proposalId, null, sda, "Sda asks");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SetVisibilityAsync(sdaChat, sda, ChatVisibility.Shared));
    }

    [Fact]
    public async Task Outsiders_cannot_chat()
    {
        var (_, _, proposalId) = await SetupAsync();
        var chat = NewChat(new FakeAiService());

        await Assert.ThrowsAnyAsync<Exception>(
            () => chat.AskAsync(proposalId, null, Guid.NewGuid(), "Hi"));
    }

    [Fact]
    public async Task Private_chats_are_invisible_to_teammates()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());

        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Private thought");

        Assert.Empty(await chat.ListAsync(proposalId, sda));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.GetMessagesAsync(chatId, sda));
    }

    [Fact]
    public async Task A_chat_can_be_shared_from_its_first_question()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());

        // The picker on a new chat says who it is for before there is a chat to change.
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "For the team",
            visibility: ChatVisibility.Shared);

        var shared = Assert.Single(await chat.ListAsync(proposalId, sda));
        Assert.Equal(chatId, shared.Id);
        Assert.Equal(ChatVisibility.Shared, shared.Visibility);
        // Nobody had to open it first: the question itself is what sda can see.
        Assert.Equal("For the team", (await chat.GetMessagesAsync(chatId, sda))[0].Text);
    }

    [Fact]
    public async Task A_reader_cannot_start_a_chat_already_shared()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Reader);
        var chat = NewChat(new FakeAiService());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.AskAsync(proposalId, null, sda, "May I broadcast?",
                visibility: ChatVisibility.Shared));

        // Their own private chat is still theirs to start, and nothing was left behind.
        var (chatId, _) = await chat.AskAsync(proposalId, null, sda, "May I ask?");
        Assert.Equal(ChatVisibility.Private, Assert.Single(await chat.ListAsync(proposalId, sda)).Visibility);
        Assert.NotEqual(Guid.Empty, chatId);
    }

    [Fact]
    public async Task Sharing_makes_a_chat_visible_and_unsharing_hides_it_again()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Worth showing");

        await chat.SetVisibilityAsync(chatId, elv, ChatVisibility.Shared);
        var shared = Assert.Single(await chat.ListAsync(proposalId, sda));
        Assert.False(shared.IsMine);
        Assert.Equal(elv, shared.OwnerId);
        Assert.NotEmpty(shared.OwnerName); // The row says whose chat it is.
        await chat.MarkSeenAsync(chatId, sda);

        await chat.SetVisibilityAsync(chatId, elv, ChatVisibility.Private);
        Assert.Empty(await chat.ListAsync(proposalId, sda));

        await using var check = db.CreateDbContext();
        // Sda's read mark is dropped, so a re-share reads as new again rather than as caught up.
        Assert.DoesNotContain(check.ChatSeen.Where(s => s.ChatSessionId == chatId), s => s.UserId == sda);
    }

    [Fact]
    public async Task Only_the_chat_owner_can_rename_share_or_delete()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        // Sda is an Editor, so the restriction is about chat ownership, not proposal role.
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Mine");
        await chat.SetVisibilityAsync(chatId, elv, ChatVisibility.Shared);

        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.RenameAsync(chatId, sda, "Yours"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SetVisibilityAsync(chatId, sda, ChatVisibility.Private));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.DeleteAsync(chatId, sda));

        await chat.RenameAsync(chatId, elv, "Renamed by its owner");
        Assert.Equal("Renamed by its owner", (await chat.GetChatAsync(chatId, elv)).Title);
    }

    [Fact]
    public async Task Proposal_owner_can_delete_someone_elses_shared_chat()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, sda, "Sda's chat");
        await chat.SetVisibilityAsync(chatId, sda, ChatVisibility.Shared);

        // Otherwise a chat belonging to someone since removed from the proposal is permanent.
        await chat.DeleteAsync(chatId, elv);
        Assert.Empty(await chat.ListAsync(proposalId, sda));
    }

    [Fact]
    public async Task Unread_appears_for_teammates_and_clears_on_mark_seen()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Look at this");
        await chat.SetVisibilityAsync(chatId, elv, ChatVisibility.Shared);

        Assert.Equal(1, await chat.UnreadCountAsync(proposalId, sda));
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv)); // Never unread on your own.

        await chat.MarkSeenAsync(chatId, sda);
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, sda));

        await chat.AskAsync(proposalId, chatId, elv, "And this");
        Assert.Equal(1, await chat.UnreadCountAsync(proposalId, sda));
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));
    }

    [Fact]
    public async Task Mark_seen_is_idempotent()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", ProposalRole.Editor);
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Once");
        await chat.SetVisibilityAsync(chatId, elv, ChatVisibility.Shared);

        await chat.MarkSeenAsync(chatId, sda);
        DateTimeOffset first;
        await using (var check = db.CreateDbContext())
            first = check.ChatSeen.Single(s => s.ChatSessionId == chatId && s.UserId == sda).LastSeenAt;

        await chat.MarkSeenAsync(chatId, sda);
        await using var recheck = db.CreateDbContext();
        var row = Assert.Single(recheck.ChatSeen.Where(s => s.ChatSessionId == chatId && s.UserId == sda));
        Assert.Equal(first, row.LastSeenAt);
    }

    [Fact]
    public async Task Deleting_a_chat_removes_its_messages_and_seen_rows()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "Doomed");

        await chat.DeleteAsync(chatId, elv);

        await using var check = db.CreateDbContext();
        Assert.Empty(check.ChatMessages.Where(m => m.ChatSessionId == chatId));
        Assert.Empty(check.ChatSeen.Where(s => s.ChatSessionId == chatId));
    }

    [Fact]
    public async Task Answer_is_dropped_when_the_chat_is_deleted_mid_stream()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat(new FakeAiService());
        var (chatId, _) = await chat.AskAsync(proposalId, null, elv, "First");

        var deleted = false;
        var (_, answer) = await chat.AskAsync(proposalId, chatId, elv, "Second",
            onDelta: async _ =>
            {
                if (deleted) return;
                deleted = true;
                await chat.DeleteAsync(chatId, elv);
            });

        // The messages went with the chat, so there is nothing left to attach the answer to.
        Assert.Null(answer);
        await using var check = db.CreateDbContext();
        Assert.Empty(check.ChatMessages.Where(m => m.ChatSessionId == chatId));
    }

    [Fact]
    public async Task Chats_are_listed_by_newest_activity()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat(new FakeAiService());
        var (first, _) = await chat.AskAsync(proposalId, null, elv, "Oldest chat");
        await chat.AskAsync(proposalId, null, elv, "Newer chat");

        Assert.Equal("Newer chat", (await chat.ListAsync(proposalId, elv))[0].Title);

        await chat.AskAsync(proposalId, first, elv, "Bringing the old one back");
        Assert.Equal("Oldest chat", (await chat.ListAsync(proposalId, elv))[0].Title);
    }
}
