using Saga.Core.Domain;

namespace Saga.Core.Abstractions;

/// <summary>Which paid Azure service a call went to; the top grouping level on the usage pages.</summary>
public enum AiServiceKind
{
    AzureOpenAI = 0,
    ContentUnderstanding = 1,
}

/// <summary>What the call was for, independent of which artifact it produced.</summary>
public enum AiOperation
{
    GenerateArtifact = 0,
    GenerateContentUnit = 1,
    Chat = 2,
    ReviewDraft = 3,
    ReviewProposal = 4,
    ExtractRequirements = 5,
    CondenseDocument = 6,
    ExtractDocument = 7,
    DescribeFigure = 8,
}

/// <summary>
/// Who and what a call belongs to, so the usage decorators can attribute it without the
/// abstraction knowing anything about the database. Passed explicitly on the request rather
/// than held in ambient state.
/// </summary>
/// <param name="OperationId">
/// Groups the calls of one user-visible operation. Content generation makes one call per unit
/// and requirements extraction one per chunk; all of them share the operation's id, which is
/// also what the UI uses to mark a rejected generation.
/// </param>
/// <param name="Label">Document name, unit title or version label — shown in the call log.</param>
public record AiCallContext(
    Guid OperationId,
    AiOperation Operation,
    Guid? ProposalId,
    Guid? UserId,
    ArtifactType? ArtifactType = null,
    string? InstructionText = null,
    string? Label = null);
