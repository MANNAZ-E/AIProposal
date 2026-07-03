namespace Saga.Core.Domain;

public class ProposalMember
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public ProposalRole Role { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}
