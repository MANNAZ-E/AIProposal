namespace Saga.Core.Domain;

public enum OutputFormat
{
    PowerPoint = 0,
    Word = 1,
}

public enum ProposalRole
{
    Reader = 0,
    Editor = 1,
    Owner = 2,
}

public enum DocumentKind
{
    Upload = 0,
    Note = 1,
}

public enum ArtifactType
{
    ClientProfile = 0,
    Summary = 1,
    Requirements = 2,
    Scoping = 3,
    SolutionProposal = 4,
    Structure = 5,
    Content = 6,
    Review = 7,
}

public enum ArtifactStatus
{
    Empty = 0,
    Generating = 1,
    Generated = 2,
    Edited = 3,
}

public enum VersionOrigin
{
    Generated = 0,
    Edited = 1,
    Restored = 2,
}

public enum WorkingContextKind
{
    SourceMaterial = 0,
    Analysis = 1,
    FullProject = 2,

    /// <summary>A chat whose material was picked by hand instead of from one of the presets.</summary>
    Custom = 3,
}

public enum ChatRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>Who can see a chat: its owner only, or everyone on the proposal.</summary>
public enum ChatVisibility
{
    Private = 0,
    Shared = 1,
}

public enum GenerationOutcome
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
    Rejected = 3,
}
