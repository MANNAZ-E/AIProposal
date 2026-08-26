using Saga.Core.Domain;

namespace Saga.Core.Models;

/// <summary>One row in the dashboard proposal list.</summary>
public record ProposalListItem(
    Guid Id,
    string Title,
    string ClientName,
    string OwnerName,
    /// <summary>Null when the viewer has no seat on the bid — a super admin listing every one.</summary>
    ProposalRole? MyRole,
    bool IsOwnedByMe,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null);
