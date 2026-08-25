namespace Saga.Core.Domain;

/// <summary>
/// One message in the bid team's group chat. Unlike <see cref="ChatMessage"/> this is
/// person-to-person: there is exactly one thread per proposal, everybody on the team reads and
/// writes it, and no model ever sees it.
/// </summary>
public class TeamMessage
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

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
/// Per-user read watermark for one proposal's team thread. Per-proposal rather than per-thread
/// (as <see cref="ChatSeen"/> is) because there is only ever one team thread.
/// </summary>
public class TeamChatSeen
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
