using System.ComponentModel.DataAnnotations.Schema;
using Saga.Core.Abstractions;

namespace Saga.Core.Domain;

/// <summary>
/// Audit record for every external AI call — LLM prompt or document extraction alike.
/// Written by the usage-tracking decorators, never by the services themselves; feeds the
/// per-proposal Usage tab and the admin roll-up.
/// </summary>
public class AiUsageRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// Groups the calls of one user-visible operation: content generation makes one call per
    /// unit and requirements extraction one per chunk. Rejecting a generation marks every row
    /// sharing this id.
    /// </summary>
    public Guid OperationId { get; set; }

    /// <summary>Nullable so a future non-proposal call still has somewhere to land.</summary>
    public Guid? ProposalId { get; set; }
    public Proposal? Proposal { get; set; }

    public AiServiceKind Service { get; set; }

    /// <summary>Deployment name for LLM calls, analyzer id for Content Understanding.</summary>
    public required string Model { get; set; }

    public AiOperation Operation { get; set; }

    /// <summary>Null for chat, condensation and extraction, which produce no artifact.</summary>
    public ArtifactType? ArtifactType { get; set; }

    /// <summary>Document name, unit title or version label — what the call log shows.</summary>
    public string? Label { get; set; }

    /// <summary>Optional steering instruction the user gave for a regeneration.</summary>
    public string? InstructionText { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }

    /// <summary>
    /// What Content Understanding reported it billed, per meter — the meter follows the work it
    /// performed, so a digital Office file lands on Minimal and a PDF or screenshot on Standard.
    /// Null on LLM rows, and on an extraction where the service reported no usage at all: that is
    /// "we were not told", which is a different thing from "nothing was charged".
    /// </summary>
    public int? MinimalPages { get; set; }
    public int? BasicPages { get; set; }
    public int? StandardPages { get; set; }

    /// <summary>Only a generative analyzer produces any; pure content extraction reports 0.</summary>
    public int? ContextualizationTokens { get; set; }

    /// <summary>Pages across all three meters, for display. Null when nothing was reported.</summary>
    [NotMapped]
    public int? Pages => MinimalPages + BasicPages + StandardPages;

    /// <summary>
    /// Frozen at write time from the rates then in force, in USD because that is the currency
    /// Azure publishes. Converted to DKK for display only.
    /// </summary>
    public decimal EstimatedCostUsd { get; set; }

    public TimeSpan Duration { get; set; }
    public GenerationOutcome Outcome { get; set; }
    public string? ErrorMessage { get; set; }

    public Guid? StartedById { get; set; }
    public User? StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>The prompt as sent, so a call can be reconstructed later. Null if capture is off.</summary>
    public string? RequestText { get; set; }

    /// <summary>The model's full response, or the extracted markdown.</summary>
    public string? ResponseText { get; set; }
}
