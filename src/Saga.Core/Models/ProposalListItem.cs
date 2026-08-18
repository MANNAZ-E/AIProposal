using Saga.Core.Domain;

namespace Saga.Core.Models;

/// <summary>One row in the dashboard proposal list.</summary>
public record ProposalListItem(
    Guid Id,
    string Title,
    string ClientName,
    string OwnerName,
    ProposalRole MyRole,
    bool IsOwnedByMe,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null);
