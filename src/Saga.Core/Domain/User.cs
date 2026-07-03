namespace Saga.Core.Domain;

public class User
{
    public Guid Id { get; set; }
    public string? EntraObjectId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ProposalMember> Memberships { get; set; } = [];
}
