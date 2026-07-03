using Saga.Core.Domain;

namespace Saga.Core.Pipeline;

/// <summary>
/// The proposal's artifact dependency graph (spec §9/§19). Changing an artifact makes
/// everything downstream of it stale; generating an artifact requires its prerequisites.
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

    /// <summary>All artifacts that become stale when the given type changes (spec §19).</summary>
    public static IReadOnlyList<ArtifactType> DownstreamOf(ArtifactType type) => type switch
    {
        // Analysis-layer changes ripple through everything that builds on the analysis.
        ArtifactType.ClientProfile or ArtifactType.Summary or ArtifactType.Requirements =>
            [ArtifactType.Scoping, ArtifactType.SolutionProposal, ArtifactType.Structure, ArtifactType.Content, ArtifactType.Review],
        ArtifactType.Scoping =>
            [ArtifactType.SolutionProposal, ArtifactType.Structure, ArtifactType.Content, ArtifactType.Review],
        ArtifactType.SolutionProposal =>
            [ArtifactType.Structure, ArtifactType.Content, ArtifactType.Review],
        ArtifactType.Structure =>
            [ArtifactType.Content, ArtifactType.Review],
        ArtifactType.Content =>
            [ArtifactType.Review],
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
