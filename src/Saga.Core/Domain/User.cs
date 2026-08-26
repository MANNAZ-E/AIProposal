namespace Saga.Core.Domain;

public class User
{
    public Guid Id { get; set; }
    public string? EntraObjectId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Grants access to /admin: managing other users and reading the spend.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// The system-owner tier: edits the Mannaz voice, promotes other super admins, impersonates,
    /// and reads every bid. Always implies <see cref="IsAdmin"/> — AdminService keeps the two in
    /// step so there is no half-privileged state for the rest of the app to reason about.
    /// </summary>
    public bool IsSuperAdmin { get; set; }

    /// <summary>
    /// Soft delete: the user can no longer sign in, but their proposals, memberships and usage
    /// history stay intact so an admin can restore ("recreate") them later.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ProposalMember> Memberships { get; set; } = [];
}
