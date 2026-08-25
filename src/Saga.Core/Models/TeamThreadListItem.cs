namespace Saga.Core.Models;

/// <summary>
/// One row in the bid team's thread list. Unlike <see cref="ChatListItem"/> there is no
/// visibility and no "mine" — every thread belongs to the whole team — so the only thing a row
/// has to say about permission is whether this viewer may rename or delete it.
/// </summary>
/// <param name="CreatedByName">Null on a thread left over from the standing "Bid Chat", which
/// nobody started.</param>
public record TeamThreadListItem(
    Guid Id,
    string Title,
    Guid? CreatedById,
    string? CreatedByName,
    bool CanRename,
    bool CanDelete,
    DateTimeOffset LastMessageAt,
    bool HasUnread);
