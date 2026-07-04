namespace Saga.Core.Domain;

/// <summary>
/// One uploaded version of the final proposal — the deliverable the team edited outside Saga
/// (PowerPoint/Word, client templates, price sheets). A version is a batch of one or more
/// files reviewed together by the proposal review. Review-only: these files never enter the
/// generation or chat working context.
/// </summary>
public class FinalProposalVersion
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    /// <summary>1-based sequence within the proposal ("Version 3").</summary>
    public int Number { get; set; }

    /// <summary>Optional user label ("Final before QA").</summary>
    public string? Label { get; set; }

    /// <summary>Latest proposal-review result for this version (ProposalReviewPayload JSON). Null until reviewed.</summary>
    public string? ReviewJson { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<FinalProposalFile> Files { get; set; } = [];
}

/// <summary>One file within a final-proposal version batch.</summary>
public class FinalProposalFile
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public FinalProposalVersion? Version { get; set; }

    public required string Name { get; set; }

    /// <summary>Storage path of the original uploaded file.</summary>
    public string? OriginalFilePath { get; set; }

    public string ExtractedText { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
