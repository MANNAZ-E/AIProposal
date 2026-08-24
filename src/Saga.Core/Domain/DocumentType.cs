namespace Saga.Core.Domain;

/// <summary>
/// A per-proposal category for source material ("Client materials", "Mannaz materials", …).
/// <see cref="SortOrder"/> is also the priority order the AI is told to resolve conflicts by,
/// so the seeded defaults come first and types added later rank below them.
/// </summary>
public class DocumentType
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public required string Name { get; set; }

    /// <summary>Position in the list, and therefore the type's priority in the working context.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Document> Documents { get; set; } = [];

    /// <summary>The client's own material — the narrowest thing a chat can be pointed at.</summary>
    public const string ClientMaterialName = "Client materials";

    public const string MannazMaterialName = "Mannaz materials";

    /// <summary>The categories every new proposal starts with, in priority order.</summary>
    public static readonly string[] DefaultNames = [ClientMaterialName, MannazMaterialName];

    /// <summary>
    /// The two seeded categories are part of every proposal's shape — what the client sent and
    /// what Mannaz brought — so they cannot be removed. Only types added later can.
    /// </summary>
    public bool IsFixed => IsFixedName(Name);

    public static bool IsFixedName(string name)
        => DefaultNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static List<DocumentType> CreateDefaults(Guid proposalId, DateTimeOffset now)
        => [.. DefaultNames.Select((name, index) => new DocumentType
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            Name = name,
            SortOrder = index,
            CreatedAt = now,
        })];
}
