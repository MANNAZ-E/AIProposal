namespace Saga.Core.Domain;

public class Artifact
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public ArtifactType Type { get; set; }

    /// <summary>Markdown body for prose artifacts (summary, scoping, solution proposal, client profile).</summary>
    public string? ContentMarkdown { get; set; }

    /// <summary>Structured payload for requirements, structure, review, and content units.</summary>
    public string? ContentJson { get; set; }

    public ArtifactStatus Status { get; set; } = ArtifactStatus.Empty;
    public bool IsLocked { get; set; }

    public DateTimeOffset? GeneratedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token; conflicting saves are surfaced, never silently lost.</summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ArtifactVersion> Versions { get; set; } = [];
}
