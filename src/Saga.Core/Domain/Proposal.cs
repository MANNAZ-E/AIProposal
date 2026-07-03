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

    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ProposalMember> Members { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Artifact> Artifacts { get; set; } = [];
    public ICollection<ChatSession> ChatSessions { get; set; } = [];
    public ICollection<GenerationRun> GenerationRuns { get; set; } = [];
}
