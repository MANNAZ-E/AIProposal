using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Proposal chat (spec: Q&amp;A only in v1). A proposal has many chats; each belongs to the person
/// who started it and is either private to them or shared with the whole team.
///
/// The material a chat may read is chosen when it starts and then frozen on the row
/// (<see cref="ChatSession.ContextSnapshot"/>), so every question replays a byte-identical
/// prefix and the provider serves it from its prompt cache instead of re-charging full input
/// price for the whole tender on every follow-up. Changing the material means a new chat.
///
/// Readers may chat in their own private chats — that never modifies the proposal — but may not
/// post into a shared one.
/// </summary>
public class ChatService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai,
    WorkingContextService contextService)
{
    /// <summary>How many previous messages travel with each question as conversation history.</summary>
    private const int HistoryWindow = 20;

    public async Task<List<ChatListItem>> ListAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        return await Visible(db, proposalId, userId)
            .OrderByDescending(s => s.LastMessageAt)
            .Select(s => new ChatListItem(
                s.Id,
                s.Title,
                s.Visibility,
                s.WorkingContext,
                s.OwnerId,
                s.Owner!.DisplayName,
                s.OwnerId == userId,
                s.LastMessageAt,
                !s.Seen.Any(x => x.UserId == userId && x.LastSeenAt >= s.LastMessageAt)))
            .ToListAsync(ct);
    }

    /// <summary>Chats with messages this user has not seen — the badge on the Chat nav item.</summary>
    public async Task<int> UnreadCountAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        return await Visible(db, proposalId, userId)
            .CountAsync(s => !s.Seen.Any(x => x.UserId == userId && x.LastSeenAt >= s.LastMessageAt), ct);
    }

    public async Task<ChatSession> GetChatAsync(Guid chatId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (chat, _) = await LoadForUserAsync(db, chatId, userId, ct);
        await db.Entry(chat).Reference(c => c.Owner).LoadAsync(ct);
        return chat;
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid chatId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await LoadForUserAsync(db, chatId, userId, ct);
        return await db.ChatMessages
            .Include(m => m.Author)
            .Where(m => m.ChatSessionId == chatId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>Everything a new chat could be pointed at, for the material picker.</summary>
    public async Task<(List<Document> Documents, List<Artifact> Artifacts)> GetMaterialAsync(
        Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        var documents = await db.Documents.Where(d => d.ProposalId == proposalId)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var artifacts = await db.Artifacts
            .Where(a => a.ProposalId == proposalId && a.Status != ArtifactStatus.Empty)
            .OrderBy(a => a.Type).ToListAsync(ct);
        return (documents, artifacts);
    }

    /// <summary>
    /// Asks a question, streaming the answer via <paramref name="onDelta"/>. A null
    /// <paramref name="chatId"/> starts a new chat: the row, its frozen material and the first
    /// question are inserted together, so a chat that exists always has a question in it.
    /// <paramref name="onChatCreated"/> hands the new id back before the answer starts arriving,
    /// which is what lets the list pane show the row (and its spinner) while it streams.
    /// </summary>
    public async Task<(Guid ChatId, ChatMessage? Answer)> AskAsync(
        Guid proposalId, Guid? chatId, Guid userId, string question,
        WorkingContextKind kind = WorkingContextKind.FullProject,
        MaterialSelection? selection = null,
        Func<Guid, Task>? onChatCreated = null,
        Func<string, Task>? onDelta = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("Ask a question first.");
        question = question.Trim();
        // A caller that hands over a selection picked the material by hand; one that only names a
        // preset gets that preset's label, rather than a guess that can land on the wrong preset
        // when two of them happen to select the same things.
        var handPicked = selection is not null;

        // ---- Phase A: authorize, freeze the material if this is a new chat, save the question.
        // No SQL connection is held across the model call in phase B, and none is held while
        // condensation runs — that is itself an AI call and can take a while.
        Guid resolvedChatId;
        string snapshot;
        string title;
        WorkingContextKind chatKind;
        bool isShared;
        bool created = chatId is null;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (chatId is null)
            {
                await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
                resolvedChatId = Guid.NewGuid();
                title = ChatTitle.FromQuestion(question);
                chatKind = kind;
                isShared = false;
                snapshot = "";
            }
            else
            {
                var (chat, role) = await LoadForUserAsync(db, chatId.Value, userId, ct);
                if (chat.OwnerId != userId && role < ProposalRole.Editor)
                    throw new InvalidOperationException("You have read-only access to this proposal.");
                resolvedChatId = chat.Id;
                title = chat.Title;
                chatKind = chat.WorkingContext;
                isShared = chat.Visibility == ChatVisibility.Shared;
                snapshot = chat.ContextSnapshot;
                selection = MaterialSelection.FromJson(chat.MaterialSelectionJson);
                proposalId = chat.ProposalId;
            }
        }

        // A chat migrated from the single-thread era has no frozen material yet, so the first
        // question after the migration freezes it against the material as it is now.
        if (snapshot.Length == 0)
        {
            var (documents, artifacts) = await GetMaterialAsync(proposalId, userId, ct);
            selection ??= MaterialSelection.ForPreset(kind, documents, artifacts);
            if (selection.IsEmpty)
                throw new InvalidOperationException(
                    "Pick at least one document, note or artifact for the chat to read.");

            var loaded = await contextService.LoadForSelectionAsync(proposalId, selection, userId, ct);
            // The selection is the only gate, so the builder is told not to filter again.
            snapshot = WorkingContextBuilder.Build(WorkingContextKind.FullProject,
                loaded.Documents, loaded.Artifacts, useCondensedDocuments: loaded.UseCondensed);
            if (created && handPicked)
                chatKind = selection.PresetOrCustom(documents, artifacts);
        }

        var proposal = await LoadProposalAsync(proposalId, ct);
        var askedAt = DateTimeOffset.UtcNow;
        List<ChatMessage> history;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (created)
            {
                db.ChatSessions.Add(new ChatSession
                {
                    Id = resolvedChatId,
                    ProposalId = proposalId,
                    OwnerId = userId,
                    Title = title,
                    Visibility = ChatVisibility.Private,
                    WorkingContext = chatKind,
                    MaterialSelectionJson = selection!.ToJson(),
                    ContextSnapshot = snapshot,
                    CreatedAt = askedAt,
                    LastMessageAt = askedAt,
                });
                history = [];
            }
            else
            {
                var chat = await db.ChatSessions.FirstAsync(s => s.Id == resolvedChatId, ct);
                chat.ContextSnapshot = snapshot;
                chat.MaterialSelectionJson = selection?.ToJson() ?? chat.MaterialSelectionJson;
                if (chat.LastMessageAt < askedAt) chat.LastMessageAt = askedAt;
                history = await db.ChatMessages
                    .Include(m => m.Author)
                    .Where(m => m.ChatSessionId == resolvedChatId)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(HistoryWindow)
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync(ct);
            }

            // The question is saved regardless of whether the answer succeeds.
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = resolvedChatId,
                Role = ChatRole.User,
                Text = question,
                WorkingContext = chatKind,
                AuthorId = userId,
                CreatedAt = askedAt,
            });
            await MarkSeenAsync(db, resolvedChatId, userId, askedAt, ct);
            await db.SaveChangesAsync(ct);
        }

        if (created && onChatCreated is not null) await onChatCreated(resolvedChatId);

        // ---- Phase B: the model call. Nothing of ours is open while this runs.
        var messages = new List<AiMessage>
        {
            AiMessage.User($"<working_context>\n{snapshot}\n</working_context>"),
            AiMessage.Assistant("Understood. I will answer questions strictly from this context."),
        };
        foreach (var m in history)
        {
            // In a shared chat several people ask, so history says who did; it sits after the
            // cached prefix, so naming the author costs no cache hits.
            var author = isShared && m.Role == ChatRole.User && m.Author is not null
                ? $"[{m.Author.DisplayName}] "
                : "";
            messages.Add(m.Role == ChatRole.User
                ? AiMessage.User(author + m.Text)
                : AiMessage.Assistant(m.Text));
        }
        messages.Add(AiMessage.User(question));

        var request = new AiRequest(ChatPrompts.BuildSystemPrompt(proposal, chatKind), messages,
            Context: new AiCallContext(Guid.NewGuid(), AiOperation.Chat, proposalId, userId, Label: title));

        var text = new System.Text.StringBuilder();
        await foreach (var evt in ai.StreamAsync(request, ct))
        {
            if (evt is AiStreamEvent.Delta d)
            {
                text.Append(d.Text);
                if (onDelta is not null) await onDelta(d.Text);
            }
        }

        // ---- Phase C: persist the answer. Never cancelled — the answer is already paid for.
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var chat = await db.ChatSessions
                .FirstOrDefaultAsync(s => s.Id == resolvedChatId, CancellationToken.None);
            // Deleted mid-stream: the messages went with it, so there is nothing to attach to.
            if (chat is null) return (resolvedChatId, null);

            var answeredAt = DateTimeOffset.UtcNow;
            var answer = new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = resolvedChatId,
                Role = ChatRole.Assistant,
                Text = text.ToString().Trim(),
                WorkingContext = chatKind,
                CreatedAt = answeredAt,
            };
            db.ChatMessages.Add(answer);
            // Monotonic: another asker in the same shared chat may have written a newer value.
            if (chat.LastMessageAt < answeredAt) chat.LastMessageAt = answeredAt;
            await MarkSeenAsync(db, resolvedChatId, userId, answeredAt, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            return (resolvedChatId, answer);
        }
    }

    public async Task RenameAsync(Guid chatId, Guid userId, string title, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0) throw new InvalidOperationException("A chat needs a title.");
        if (title.Length > 200) title = title[..200];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (chat, _) = await LoadForUserAsync(db, chatId, userId, ct);
        EnsureOwner(chat, userId, "rename");
        chat.Title = title;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetVisibilityAsync(Guid chatId, Guid userId, ChatVisibility visibility,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (chat, role) = await LoadForUserAsync(db, chatId, userId, ct);
        EnsureOwner(chat, userId, "share");
        if (visibility == ChatVisibility.Shared && role < ProposalRole.Editor)
            throw new InvalidOperationException("You have read-only access to this proposal.");
        if (chat.Visibility == visibility) return;

        chat.Visibility = visibility;
        if (visibility == ChatVisibility.Private)
        {
            // Drop everyone else's read marks, or a later re-share arrives already "read" and
            // the people it was shared with never see that it is new.
            var stale = await db.ChatSeen
                .Where(s => s.ChatSessionId == chatId && s.UserId != chat.OwnerId)
                .ToListAsync(ct);
            db.ChatSeen.RemoveRange(stale);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Hard delete — messages and read marks cascade. Unlike a proposal, a chat is not proposal
    /// content, and a soft delete would put a filter on every chat query for nothing. The cost
    /// history survives: AiUsageRecord hangs off the proposal, not the chat.
    /// </summary>
    public async Task DeleteAsync(Guid chatId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (chat, role) = await LoadForUserAsync(db, chatId, userId, ct);
        // The proposal owner can delete anyone's chat. Without that, a chat belonging to someone
        // who has since been removed from the proposal could never be cleaned up.
        if (chat.OwnerId != userId && role != ProposalRole.Owner)
            throw new InvalidOperationException("Only the person who started a chat can delete it.");
        db.ChatSessions.Remove(chat);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkSeenAsync(Guid chatId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (chat, _) = await LoadForUserAsync(db, chatId, userId, ct);
        if (!await MarkSeenAsync(db, chatId, userId, chat.LastMessageAt, ct)) return;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves this user's read mark forward. Returns false when it was already caught up, which is
    /// what makes the method safe to call from a component lifecycle method: no write per render.
    /// </summary>
    private static async Task<bool> MarkSeenAsync(SagaDbContext db, Guid chatId, Guid userId,
        DateTimeOffset seenAt, CancellationToken ct)
    {
        var seen = await db.ChatSeen.FirstOrDefaultAsync(s => s.ChatSessionId == chatId && s.UserId == userId, ct);
        if (seen is null)
        {
            db.ChatSeen.Add(new ChatSeen
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                UserId = userId,
                LastSeenAt = seenAt,
            });
            return true;
        }
        if (seen.LastSeenAt >= seenAt) return false;
        seen.LastSeenAt = seenAt;
        return true;
    }

    /// <summary>Your own chats plus every chat shared with the team.</summary>
    private static IQueryable<ChatSession> Visible(SagaDbContext db, Guid proposalId, Guid userId)
        => db.ChatSessions.Where(s => s.ProposalId == proposalId
                                      && (s.OwnerId == userId || s.Visibility == ChatVisibility.Shared));

    /// <summary>Loads a chat the user is allowed to see, along with their role on the proposal.</summary>
    private static async Task<(ChatSession Chat, ProposalRole Role)> LoadForUserAsync(
        SagaDbContext db, Guid chatId, Guid userId, CancellationToken ct)
    {
        var chat = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == chatId, ct)
            // A business condition, not an auth failure: the UI shows this sentence verbatim.
            ?? throw new InvalidOperationException("That chat no longer exists.");
        var role = await ProposalService.RequireRoleAsync(db, chat.ProposalId, userId, ProposalRole.Reader, ct);
        if (chat.Visibility != ChatVisibility.Shared && chat.OwnerId != userId)
            throw new UnauthorizedAccessException("This chat is private.");
        return (chat, role);
    }

    private static void EnsureOwner(ChatSession chat, Guid userId, string action)
    {
        if (chat.OwnerId != userId)
            throw new InvalidOperationException($"Only the person who started a chat can {action} it.");
    }

    private async Task<Proposal> LoadProposalAsync(Guid proposalId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
    }
}
