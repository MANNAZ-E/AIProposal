using Saga.Core.Domain;
using Saga.Core.Pipeline;

namespace Saga.Tests;

public class PipelineTests
{
    [Theory]
    // Spec §19 staleness table.
    [InlineData(ArtifactType.Scoping, new[] { ArtifactType.SolutionProposal, ArtifactType.Structure, ArtifactType.Content, ArtifactType.Review })]
    [InlineData(ArtifactType.SolutionProposal, new[] { ArtifactType.Structure, ArtifactType.Content, ArtifactType.Review })]
    [InlineData(ArtifactType.Structure, new[] { ArtifactType.Content, ArtifactType.Review })]
    [InlineData(ArtifactType.Content, new[] { ArtifactType.Review })]
    [InlineData(ArtifactType.Review, new ArtifactType[0])]
    public void Downstream_matches_spec(ArtifactType changed, ArtifactType[] expected)
    {
        Assert.Equal(expected, ArtifactDependencies.DownstreamOf(changed));
    }

    [Fact]
    public void Missing_prerequisites_recommends_only_what_is_absent()
    {
        var existing = new HashSet<ArtifactType> { ArtifactType.Summary };
        var selected = new HashSet<ArtifactType> { ArtifactType.Requirements };

        var missing = ArtifactDependencies.MissingPrerequisites(ArtifactType.Scoping, existing, selected);

        Assert.Empty(missing); // Summary exists, Requirements is already selected.

        missing = ArtifactDependencies.MissingPrerequisites(ArtifactType.Scoping,
            new HashSet<ArtifactType>(), new HashSet<ArtifactType>());
        Assert.Equal([ArtifactType.Summary, ArtifactType.Requirements], missing);
    }

    [Fact]
    public void Source_material_context_contains_documents_and_notes_but_no_artifacts()
    {
        var documents = new List<Document>
        {
            new() { Name = "tender.pdf", Kind = DocumentKind.Upload, ExtractedText = "TENDER-TEXT" },
            new() { Name = "kickoff", Kind = DocumentKind.Note, ExtractedText = "NOTE-TEXT" },
        };
        var artifacts = new List<Artifact>
        {
            new() { Type = ArtifactType.Summary, Status = ArtifactStatus.Generated, ContentMarkdown = "SUMMARY-TEXT" },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.SourceMaterial, documents, artifacts);

        Assert.Contains("TENDER-TEXT", context);
        Assert.Contains("NOTE-TEXT", context);
        Assert.DoesNotContain("SUMMARY-TEXT", context);
        // Client documents come before notes: priority order is visible in the prompt.
        Assert.True(context.IndexOf("TENDER-TEXT") < context.IndexOf("NOTE-TEXT"));
    }

    [Fact]
    public void Analysis_context_includes_analysis_artifacts_but_not_downstream_ones()
    {
        var artifacts = new List<Artifact>
        {
            new() { Type = ArtifactType.Summary, Status = ArtifactStatus.Generated, ContentMarkdown = "SUMMARY-TEXT" },
            new() { Type = ArtifactType.Scoping, Status = ArtifactStatus.Generated, ContentMarkdown = "SCOPING-TEXT" },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.Analysis, [], artifacts);

        Assert.Contains("SUMMARY-TEXT", context);
        Assert.DoesNotContain("SCOPING-TEXT", context);
    }

    [Fact]
    public void Full_project_context_excludes_the_artifact_being_regenerated()
    {
        var artifacts = new List<Artifact>
        {
            new() { Type = ArtifactType.Summary, Status = ArtifactStatus.Generated, ContentMarkdown = "SUMMARY-TEXT" },
            new() { Type = ArtifactType.Content, Status = ArtifactStatus.Generated, ContentMarkdown = "CONTENT-TEXT" },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.FullProject, [], artifacts,
            excludeArtifact: ArtifactType.Content);

        Assert.Contains("SUMMARY-TEXT", context);
        Assert.DoesNotContain("CONTENT-TEXT", context);
    }

    [Fact]
    public void Empty_artifacts_are_never_included()
    {
        var artifacts = new List<Artifact>
        {
            new() { Type = ArtifactType.Summary, Status = ArtifactStatus.Empty, ContentMarkdown = "LEFTOVER" },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.FullProject, [], artifacts);
        Assert.DoesNotContain("LEFTOVER", context);
    }
}
