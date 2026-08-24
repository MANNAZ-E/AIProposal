using Saga.Core.Domain;

namespace Saga.Core.Models;

/// <summary>One row in the chat list pane.</summary>
public record ChatListItem(
    Guid Id,
    string Title,
    ChatVisibility Visibility,
    WorkingContextKind WorkingContext,
    Guid OwnerId,
    string OwnerName,
    bool IsMine,
    DateTimeOffset LastMessageAt,
    bool HasUnread);
