namespace Saga.Core.Domain;

/// <summary>Audit record for every AI generation; feeds the usage/cost page.</summary>
public class GenerationRun
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public ArtifactType ArtifactType { get; set; }
    public required string Model { get; set; }

    /// <summary>Optional steering instruction the user gave for a regeneration.</summary>
    public string? InstructionText { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public TimeSpan Duration { get; set; }
    public GenerationOutcome Outcome { get; set; }

    public Guid? StartedById { get; set; }
    public User? StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}
