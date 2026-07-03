namespace Saga.Core.Domain;

public class Document
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public DocumentKind Kind { get; set; }
    public required string Name { get; set; }

    /// <summary>Storage path of the original uploaded file. Null for notes.</summary>
    public string? OriginalFilePath { get; set; }

    /// <summary>Extracted plain text (uploads) or the note text itself.</summary>
    public string ExtractedText { get; set; } = "";

    /// <summary>JSON map of page/section offsets into ExtractedText, from Document Intelligence. Null for notes.</summary>
    public string? PageMapJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
