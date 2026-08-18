using Saga.Core.Domain;

namespace Saga.Core.Pipeline;

/// <summary>
/// The proposal's artifact dependency graph (spec §9): generating an artifact requires
/// its prerequisites. Regeneration is the consultant's call, so nothing is flagged automatically.
/// </summary>
public static class ArtifactDependencies
{
    /// <summary>Artifacts whose content feeds directly into generating the given type.</summary>
    public static IReadOnlyList<ArtifactType> PrerequisitesOf(ArtifactType type) => type switch
    {
        ArtifactType.ClientProfile => [],
        ArtifactType.Summary => [],
        ArtifactType.Requirements => [],
        ArtifactType.Scoping => [ArtifactType.Summary, ArtifactType.Requirements],
        ArtifactType.SolutionProposal => [ArtifactType.Scoping],
        ArtifactType.Structure => [ArtifactType.SolutionProposal, ArtifactType.Requirements],
        ArtifactType.Content => [ArtifactType.Structure],
        ArtifactType.Review => [ArtifactType.Content, ArtifactType.Requirements],
        _ => [],
    };

    /// <summary>
    /// Missing prerequisites for generating <paramref name="type"/>, given which artifacts already
    /// have content. Used by checkbox generation to recommend adding prerequisites.
    /// </summary>
    public static IReadOnlyList<ArtifactType> MissingPrerequisites(ArtifactType type,
        ISet<ArtifactType> existing, ISet<ArtifactType> alreadySelected)
        => PrerequisitesOf(type)
            .Where(p => !existing.Contains(p) && !alreadySelected.Contains(p))
            .ToList();
}
