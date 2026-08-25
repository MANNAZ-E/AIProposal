namespace Saga.Core.Domain;

public class User
{
    public Guid Id { get; set; }
    public string? EntraObjectId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Grants access to /admin, including managing other users and impersonating them.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Soft delete: the user can no longer sign in, but their proposals, memberships and usage
    /// history stay intact so an admin can restore ("recreate") them later.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ProposalMember> Memberships { get; set; } = [];
}
