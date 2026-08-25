namespace Saga.Core.Domain;

/// <summary>
/// One thread in the bid team's chat. Unlike <see cref="ChatSession"/> this is person-to-person:
/// every thread belongs to the whole team, everybody on it reads and writes every thread, and no
/// model ever sees any of them. There is no private/shared distinction because being on the bid
/// team is the whole permission.
/// </summary>
public class TeamThread
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    /// <summary>Derived from the first message posted into it; renameable afterwards.</summary>
    public required string Title { get; set; }

    /// <summary>
    /// Who started it. Nullable, unlike <see cref="ChatSession.OwnerId"/>, only because of the
    /// standing "Bid Chat" thread the app used to create by itself: one that collected messages
    /// before that was dropped survives as an ordinary thread with nobody behind it. Nothing
    /// creates a thread without a creator any more.
    /// </summary>
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the newest message arrived, or <see cref="CreatedAt"/> while the thread is empty.
    /// Denormalized for the same two reasons as <see cref="ChatSession.LastMessageAt"/>: the list
    /// orders on an index, and the unread check is a NOT EXISTS instead of an aggregate over every
    /// message of every thread.
    /// </summary>
    public DateTimeOffset LastMessageAt { get; set; }

    public ICollection<TeamMessage> Messages { get; set; } = [];
    public ICollection<TeamChatSeen> Seen { get; set; } = [];
}

/// <summary>
/// One message in a bid team thread. Unlike <see cref="ChatMessage"/> this is person-to-person:
/// everybody on the team reads and writes it, and no model ever sees it.
/// </summary>
public class TeamMessage
{
    public Guid Id { get; set; }
    public Guid TeamThreadId { get; set; }
    public TeamThread? Thread { get; set; }

    /// <summary>
    /// Who wrote it. Non-nullable, unlike a chat message's author: users are never deleted in
    /// this app, and a team message with nobody behind it would read as anonymous in a
    /// conversation where who said what is the point.
    /// </summary>
    public Guid AuthorId { get; set; }
    public User? Author { get; set; }

    /// <summary>Plain text — not markdown. Newlines survive; the bubble renders pre-wrap.</summary>
    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<TeamMessageMention> Mentions { get; set; } = [];
}

/// <summary>
/// One resolved <c>@mention</c> inside a message. The offsets are what the server found when the
/// message was posted, so rendering is a pure splice and never re-runs the scanner: somebody
/// removed from the team afterwards keeps their mention bolded in the history.
///
/// It is its own row rather than a flag on the message so that the mail notification this should
/// eventually trigger is a service change, not a migration.
/// </summary>
public class TeamMessageMention
{
    public Guid Id { get; set; }
    public Guid TeamMessageId { get; set; }
    public TeamMessage? TeamMessage { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Index of the <c>@</c> in <see cref="TeamMessage.Text"/>.</summary>
    public int Start { get; set; }

    /// <summary>Length of the mention including the <c>@</c>.</summary>
    public int Length { get; set; }
}

/// <summary>
/// Per-user read watermark for one team thread; drives the unread dots on the thread list and the
/// count on the nav item. Per thread rather than per proposal, so catching up on one conversation
/// does not silently mark the others read.
/// </summary>
public class TeamChatSeen
{
    public Guid Id { get; set; }
    public Guid TeamThreadId { get; set; }
    public TeamThread? Thread { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
