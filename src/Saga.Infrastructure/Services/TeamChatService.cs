using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// The bid team's group chat: one thread per proposal, people talking to each other rather than
/// to a model. No AI call is made here and nothing is billed.
///
/// Every method takes <see cref="ProposalRole.Reader"/> and nothing more — deliberately unlike
/// <see cref="ChatService"/>, where a Reader may not post into a shared chat. Being on the bid
/// team is the whole permission: someone brought in to read the proposal still has to be able to
/// say something about it.
/// </summary>
public class TeamChatService(
    IDbContextFactory<SagaDbContext> dbFactory,
    TeamChatNotifier notifier)
{
    /// <summary>Long enough for anything anybody types in a chat box; the column is unbounded.</summary>
    private const int MaxLength = 4000;

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

    public async Task<List<TeamMessage>> ListAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.TeamMessages
            .Include(m => m.Author)
            .Include(m => m.Mentions)
            .Where(m => m.ProposalId == proposalId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Posts a message. The text is re-scanned server-side against the current bid team, so a
    /// mention typed by hand resolves exactly like one picked from the composer's list, and the
    /// offsets it found are stored with the message rather than recomputed at render time.
    /// </summary>
    public async Task<Guid> PostAsync(Guid proposalId, Guid userId, string text,
        CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new InvalidOperationException("Write something first.");
        if (text.Length > MaxLength) text = text[..MaxLength];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        // Mentioning yourself is a typo, not a notification, so you are not a candidate.
        var candidates = (await MembersAsync(db, proposalId, ct))
            .Where(m => m.UserId != userId)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var message = new TeamMessage
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
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

        // Your own message never counts as unread against you.
        await MarkSeenAsync(db, proposalId, userId, now, ct);
        await db.SaveChangesAsync(ct);

        // After the save, so a circuit that reloads on the event reads the message it was told about.
        notifier.Publish(proposalId);
        return message.Id;
    }

    /// <summary>Messages since this user's watermark that they did not write — the nav badge.</summary>
    public async Task<int> UnreadCountAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var seenAt = await db.TeamChatSeen
            .Where(s => s.ProposalId == proposalId && s.UserId == userId)
            .Select(s => (DateTimeOffset?)s.LastSeenAt)
            .FirstOrDefaultAsync(ct);

        return await db.TeamMessages.CountAsync(
            m => m.ProposalId == proposalId
                 && m.AuthorId != userId
                 && (seenAt == null || m.CreatedAt > seenAt), ct);
    }

    public async Task MarkSeenAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var latest = await db.TeamMessages
            .Where(m => m.ProposalId == proposalId)
            .MaxAsync(m => (DateTimeOffset?)m.CreatedAt, ct);
        if (latest is null) return;

        if (!await MarkSeenAsync(db, proposalId, userId, latest.Value, ct)) return;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves this user's read mark forward. Returns false when it was already caught up, which is
    /// what makes the method safe to call from a component lifecycle method: no write per render.
    /// </summary>
    private static async Task<bool> MarkSeenAsync(SagaDbContext db, Guid proposalId, Guid userId,
        DateTimeOffset seenAt, CancellationToken ct)
    {
        var seen = await db.TeamChatSeen
            .FirstOrDefaultAsync(s => s.ProposalId == proposalId && s.UserId == userId, ct);
        if (seen is null)
        {
            db.TeamChatSeen.Add(new TeamChatSeen
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
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
