namespace Saga.Core.Domain;

public class ChatSession
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ChatSessionId { get; set; }
    public ChatSession? ChatSession { get; set; }

    public ChatRole Role { get; set; }
    public required string Text { get; set; }

    /// <summary>Which knowledge base the AI was allowed to use for this exchange.</summary>
    public WorkingContextKind WorkingContext { get; set; }

    public Guid? AuthorId { get; set; }
    public User? Author { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
