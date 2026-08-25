namespace Saga.Core.Domain;

public class Proposal
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string ClientName { get; set; }
    public string? Description { get; set; }
    public OutputFormat OutputFormat { get; set; } = OutputFormat.PowerPoint;

    /// <summary>
    /// Language for generated content (e.g. "da", "en"). Null = auto-detect from client material.
    /// </summary>
    public string? ContentLanguage { get; set; }

    /// <summary>
    /// Client name to search for when researching the client profile. Null = use <see cref="ClientName"/>.
    /// The legal or trading name is often more searchable than the name used on the proposal.
    /// </summary>
    public string? ResearchClientName { get; set; }

    /// <summary>Optional client website, used to anchor the client-profile web search.</summary>
    public string? ClientWebsite { get; set; }

    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    public bool IsArchived { get; set; }

    /// <summary>
    /// Soft delete: the proposal leaves the dashboard but stays in the database, so any team
    /// member (or an admin) can restore it from the recycle bin.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ProposalMember> Members { get; set; } = [];
    public ICollection<DocumentType> DocumentTypes { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Artifact> Artifacts { get; set; } = [];
    public ICollection<ChatSession> ChatSessions { get; set; } = [];
    public ICollection<TeamMessage> TeamMessages { get; set; } = [];
    public ICollection<AiUsageRecord> AiUsage { get; set; } = [];
    public ICollection<FinalProposalVersion> FinalProposalVersions { get; set; } = [];
}
