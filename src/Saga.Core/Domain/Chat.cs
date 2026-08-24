namespace Saga.Core.Domain;

/// <summary>
/// One chat thread on a proposal. The material a chat may read is chosen when it starts and
/// then frozen (<see cref="ContextSnapshot"/>), so every question in the chat re-sends a
/// byte-identical prefix and the provider serves it from its prompt cache. Changing the
/// material means starting a new chat.
/// </summary>
public class ChatSession
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    /// <summary>Who started the chat. Only the owner may rename it or change its visibility.</summary>
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>Derived from the first question; renameable by the owner.</summary>
    public required string Title { get; set; }

    public ChatVisibility Visibility { get; set; }

    /// <summary>
    /// The preset the material selection came from, or <see cref="WorkingContextKind.Custom"/>.
    /// Deliberately initialized rather than left at the enum's zero: the zero is SourceMaterial,
    /// but a new chat sees everything unless the user says otherwise.
    /// </summary>
    public WorkingContextKind WorkingContext { get; set; } = WorkingContextKind.FullProject;

    /// <summary>
    /// The chosen material as JSON: <c>{"documentIds":[â€¦],"artifactTypes":[â€¦]}</c>. Nothing
    /// queries into it â€” it exists to render what the chat can see and to seed a follow-up chat.
    /// </summary>
    public string MaterialSelectionJson { get; set; } = "";

    /// <summary>
    /// The assembled working-context block, frozen when the chat started. Empty means "not built
    /// yet" (chats migrated from the single-thread era), which the next question re-freezes.
    /// </summary>
    public string ContextSnapshot { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the newest message arrived. Denormalized so the list can order on an index and the
    /// unread check is a NOT EXISTS against <see cref="ChatSeen"/> instead of an aggregate over
    /// every message of every chat.
    /// </summary>
    public DateTimeOffset LastMessageAt { get; set; }

    public ICollection<ChatMessage> Messages { get; set; } = [];
    public ICollection<ChatSeen> Seen { get; set; } = [];
}

/// <summary>Per-user read watermark for one chat; drives the unread dots on the chat list.</summary>
public class ChatSeen
{
    public Guid Id { get; set; }
    public Guid ChatSessionId { get; set; }
    public ChatSession? ChatSession { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
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
