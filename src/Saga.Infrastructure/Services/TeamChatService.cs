using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// The bid team's group chat: threads of people talking to each other rather than to a model.
/// No AI call is made here and nothing is billed.
///
/// Every thread belongs to the whole team — deliberately unlike <see cref="ChatService"/>, where a
/// chat is private until shared and a Reader may not post into someone else's. Here every method
/// takes <see cref="ProposalRole.Reader"/> and nothing more: being on the bid team is the whole
/// permission, and someone brought in to read the proposal still has to be able to say something
/// about it. Only renaming and deleting a thread ask for more than that.
/// </summary>
public class TeamChatService(
    IDbContextFactory<SagaDbContext> dbFactory,
    TeamChatNotifier notifier)
{
    /// <summary>Long enough for anything anybody types in a chat box; the column is unbounded.</summary>
    private const int MaxLength = 4000;

    /// <summary>Matches ChatSession.Title, and the column.</summary>
    private const int MaxTitleLength = 200;

    /// <summary>How many teammate colours the palette cycles through (own messages take the fourth).</summary>
    private const int ColourSlots = 3;

    /// <summary>
    /// The bid team, in the order they joined. The position is what the colour is taken from, so
    /// it has to be stable and total — hence <c>UserId</c> behind <c>AddedAt</c>, since the owner
    /// and an invitee added in the same batch can share a timestamp.
    /// </summary>
    public async Task<List<TeamChatMember>> MembersAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await MembersAsync(db, proposalId, ct);
    }

    /// <summary>
    /// The proposal's threads, newest conversation first. A proposal with none lists none: the
    /// section opens on an unsaved draft instead, the way a new chat does, so nothing is created
    /// until somebody actually has something to say.
    /// </summary>
    public async Task<List<TeamThreadListItem>> ListThreadsAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var role = await ProposalService.RequireRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var rows = await db.TeamThreads
            .Where(t => t.ProposalId == proposalId)
            .OrderByDescending(t => t.LastMessageAt)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.CreatedById,
                CreatedByName = t.CreatedBy!.DisplayName,
                t.LastMessageAt,
                // A thread always holds at least the message that started it, and posting moves
                // the poster's own watermark, so the watermark alone decides this.
                HasUnread = !t.Seen.Any(s => s.UserId == userId && s.LastSeenAt >= t.LastMessageAt),
            })
            .ToListAsync(ct);

        return rows
            .Select(t => new TeamThreadListItem(
                t.Id, t.Title, t.CreatedById,
                t.CreatedById is null ? null : t.CreatedByName,
                CanRename: CanManage(t.CreatedById, userId, role),
                CanDelete: CanManage(t.CreatedById, userId, role),
                t.LastMessageAt, t.HasUnread))
            .ToList();
    }

    public async Task<List<TeamMessage>> ListAsync(Guid threadId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await LoadThreadAsync(db, threadId, userId, ct);
        return await db.TeamMessages
            .Include(m => m.Author)
            .Include(m => m.Mentions)
            .Where(m => m.TeamThreadId == threadId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Starts a thread and posts its first message in one save, so a thread that exists always has
    /// something in it — the same reason a chat is created by its first question. The title comes
    /// from that message; nobody is asked to name a thread before they know what it is about.
    /// </summary>
    public async Task<Guid> StartThreadAsync(Guid proposalId, Guid userId, string text,
        CancellationToken ct = default)
    {
        text = Clean(text);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var now = DateTimeOffset.UtcNow;
        var thread = new TeamThread
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Title = ChatTitle.FromQuestion(text, "New chat"),
            CreatedById = userId,
            CreatedAt = now,
            LastMessageAt = now,
        };
        db.TeamThreads.Add(thread);

        await AddMessageAsync(db, thread, proposalId, userId, text, now, ct);
        await db.SaveChangesAsync(ct);

        notifier.Publish(proposalId, thread.Id);
        return thread.Id;
    }

    /// <summary>
    /// Posts a message into an existing thread. The text is re-scanned server-side against the
    /// current bid team, so a mention typed by hand resolves exactly like one picked from the
    /// composer's list, and the offsets it found are stored with the message rather than
    /// recomputed at render time.
    /// </summary>
    public async Task<Guid> PostAsync(Guid threadId, Guid userId, string text,
        CancellationToken ct = default)
    {
        text = Clean(text);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (thread, _) = await LoadThreadAsync(db, threadId, userId, ct);

        var now = DateTimeOffset.UtcNow;
        var message = await AddMessageAsync(db, thread, thread.ProposalId, userId, text, now, ct);
        await db.SaveChangesAsync(ct);

        // After the save, so a circuit that reloads on the event reads the message it was told about.
        notifier.Publish(thread.ProposalId, threadId);
        return message.Id;
    }

    public async Task RenameAsync(Guid threadId, Guid userId, string title, CancellationToken ct = default)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw new InvalidOperationException("A chat needs a title.");
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (thread, role) = await LoadThreadAsync(db, threadId, userId, ct);
        EnsureCanManage(thread, userId, role, deleting: false);

        thread.Title = title;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a thread and everything in it. Every thread may go: a proposal with none opens on a
    /// draft, so there is nothing to protect by keeping the last one alive.
    /// </summary>
    public async Task DeleteAsync(Guid threadId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (thread, role) = await LoadThreadAsync(db, threadId, userId, ct);
        EnsureCanManage(thread, userId, role, deleting: true);

        db.TeamThreads.Remove(thread);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Messages across every thread of the proposal that this user did not write and has not read
    /// — the nav badge. It counts messages rather than threads so the number keeps meaning what it
    /// meant when there was only one thread.
    /// </summary>
    public async Task<int> UnreadCountAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        return await db.TeamMessages.CountAsync(
            m => m.Thread!.ProposalId == proposalId
                 && m.AuthorId != userId
                 && !db.TeamChatSeen.Any(s => s.TeamThreadId == m.TeamThreadId
                                              && s.UserId == userId
                                              && s.LastSeenAt >= m.CreatedAt), ct);
    }

    public async Task MarkSeenAsync(Guid threadId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await LoadThreadAsync(db, threadId, userId, ct);

        var latest = await db.TeamMessages
            .Where(m => m.TeamThreadId == threadId)
            .MaxAsync(m => (DateTimeOffset?)m.CreatedAt, ct);
        if (latest is null) return;

        if (!await MarkSeenAsync(db, threadId, userId, latest.Value, ct)) return;
        await db.SaveChangesAsync(ct);
    }

    private static string Clean(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new InvalidOperationException("Write something first.");
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    /// <summary>
    /// The message, its resolved mentions and the poster's own read mark — everything that makes
    /// one post, minus the save, so starting a thread and posting into one share it.
    /// </summary>
    private static async Task<TeamMessage> AddMessageAsync(SagaDbContext db, TeamThread thread,
        Guid proposalId, Guid userId, string text, DateTimeOffset now, CancellationToken ct)
    {
        // Mentioning yourself is a typo, not a notification, so you are not a candidate.
        var candidates = (await MembersAsync(db, proposalId, ct))
            .Where(m => m.UserId != userId)
            .ToList();

        var message = new TeamMessage
        {
            Id = Guid.NewGuid(),
            TeamThreadId = thread.Id,
            AuthorId = userId,
            Text = text,
            CreatedAt = now,
        };
        db.TeamMessages.Add(message);

        foreach (var match in MentionScanner.Scan(text, candidates))
        {
            db.TeamMessageMentions.Add(new TeamMessageMention
            {
                Id = Guid.NewGuid(),
                TeamMessageId = message.Id,
                UserId = match.UserId,
                Start = match.Start,
                Length = match.Length,
            });
        }

        // Monotonic, so two people posting into one thread cannot move it backwards in the list.
        if (now > thread.LastMessageAt) thread.LastMessageAt = now;

        // Your own message never counts as unread against you.
        await MarkSeenAsync(db, thread.Id, userId, now, ct);
        return message;
    }

    /// <summary>
    /// Loads a thread the user is on the bid team for, along with their role on the proposal.
    /// There is no per-thread visibility to check — the team sees every thread.
    /// </summary>
    private static async Task<(TeamThread Thread, ProposalRole Role)> LoadThreadAsync(
        SagaDbContext db, Guid threadId, Guid userId, CancellationToken ct)
    {
        var thread = await db.TeamThreads.FirstOrDefaultAsync(t => t.Id == threadId, ct)
            // A business condition, not an auth failure: the UI shows this sentence verbatim.
            ?? throw new InvalidOperationException("That chat no longer exists.");
        var role = await ProposalService.RequireRoleAsync(db, thread.ProposalId, userId, ProposalRole.Reader, ct);
        return (thread, role);
    }

    /// <summary>
    /// Who may rename or delete a thread: the person who started it, or the proposal owner —
    /// without the second, a thread started by somebody since removed from the team, or one left
    /// over from the old standing thread and so started by nobody, could never be cleaned up.
    /// </summary>
    private static bool CanManage(Guid? createdById, Guid userId, ProposalRole role)
        => createdById == userId || role == ProposalRole.Owner;

    private static void EnsureCanManage(TeamThread thread, Guid userId, ProposalRole role, bool deleting)
    {
        if (CanManage(thread.CreatedById, userId, role)) return;
        throw new InvalidOperationException(
            $"Only the person who started a chat, or the proposal owner, can {(deleting ? "delete" : "rename")} it.");
    }

    private static async Task<bool> MarkSeenAsync(SagaDbContext db, Guid threadId, Guid userId,
        DateTimeOffset seenAt, CancellationToken ct)
    {
        var seen = await db.TeamChatSeen
            .FirstOrDefaultAsync(s => s.TeamThreadId == threadId && s.UserId == userId, ct);
        if (seen is null)
        {
            db.TeamChatSeen.Add(new TeamChatSeen
            {
                Id = Guid.NewGuid(),
                TeamThreadId = threadId,
                UserId = userId,
                LastSeenAt = seenAt,
            });
            return true;
        }
        if (seen.LastSeenAt >= seenAt) return false;
        seen.LastSeenAt = seenAt;
        return true;
    }

    private static async Task<List<TeamChatMember>> MembersAsync(SagaDbContext db, Guid proposalId,
        CancellationToken ct)
    {
        var members = await db.ProposalMembers
            .Where(m => m.ProposalId == proposalId)
            .OrderBy(m => m.AddedAt).ThenBy(m => m.UserId)
            .Select(m => new { m.UserId, m.User!.DisplayName, m.User.Email })
            .ToListAsync(ct);

        return members
            .Select((m, index) => new TeamChatMember(m.UserId, m.DisplayName, m.Email, index % ColourSlots))
            .ToList();
    }
}
