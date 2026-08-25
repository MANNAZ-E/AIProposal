using Saga.Core.Domain;
using Saga.Core.Pipeline;

namespace Saga.Tests;

public class PipelineTests
{
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
        var client = new DocumentType { Name = "Client materials", SortOrder = 0 };
        var documents = new List<Document>
        {
            new() { Name = "tender.pdf", Kind = DocumentKind.Upload, ExtractedText = "TENDER-TEXT", DocumentType = client },
            new() { Name = "kickoff", Kind = DocumentKind.Note, ExtractedText = "NOTE-TEXT", DocumentType = client },
        };
        var artifacts = new List<Artifact>
        {
            new() { Type = ArtifactType.Summary, Status = ArtifactStatus.Generated, ContentMarkdown = "SUMMARY-TEXT" },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.SourceMaterial, documents, artifacts);

        Assert.Contains("TENDER-TEXT", context);
        Assert.Contains("NOTE-TEXT", context);
        Assert.DoesNotContain("SUMMARY-TEXT", context);
        // Client material comes before notes: priority order is visible in the prompt.
        Assert.True(context.IndexOf("TENDER-TEXT") < context.IndexOf("NOTE-TEXT"));
    }

    [Fact]
    public void Documents_are_grouped_by_type_in_the_types_own_priority_order()
    {
        var client = new DocumentType { Name = "Client materials", SortOrder = 0 };
        var mannaz = new DocumentType { Name = "Mannaz materials", SortOrder = 1 };
        var documents = new List<Document>
        {
            // Deliberately added lowest-priority first: the type's order decides, not insertion.
            new() { Name = "offering.pdf", Kind = DocumentKind.Upload, ExtractedText = "MANNAZ-TEXT", DocumentType = mannaz },
            new() { Name = "tender.pdf", Kind = DocumentKind.Upload, ExtractedText = "CLIENT-TEXT", DocumentType = client },
            new() { Name = "kickoff", Kind = DocumentKind.Note, ExtractedText = "NOTE-TEXT", DocumentType = mannaz },
        };

        var context = WorkingContextBuilder.Build(WorkingContextKind.SourceMaterial, documents, []);

        Assert.Contains("<category name=\"Client materials\">", context);
        Assert.Contains("<category name=\"Mannaz materials\">", context);
        Assert.True(context.IndexOf("CLIENT-TEXT") < context.IndexOf("MANNAZ-TEXT"));
        // A note keeps its category as an attribute but stays in the lower-priority notes block.
        Assert.Contains("category=\"Mannaz materials\"", context);
        Assert.True(context.IndexOf("MANNAZ-TEXT") < context.IndexOf("NOTE-TEXT"));
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

    [Fact]
    public void Token_budget_passes_small_material_untouched()
    {
        // Counts are set explicitly: this is a test of the budget policy, not of the tokenizer.
        var documents = new List<Document>
        {
            new() { Name = "small.pdf", Kind = DocumentKind.Upload, ExtractedText = "small", TokenCount = 100 },
        };

        var status = TokenBudget.Assess(documents, budget: 1000);

        Assert.False(status.OverBudget);
        Assert.False(status.UsingCondensed);
        Assert.Equal(100, status.Tokens);
    }

    [Fact]
    public void Token_budget_reports_condensed_fallback_only_when_it_fits()
    {
        var oversized = new List<Document>
        {
            new()
            {
                Name = "big.pdf", Kind = DocumentKind.Upload,
                ExtractedText = "big", TokenCount = 2000,
                CondensedText = "condensed", CondensedTokenCount = 100,
            },
        };

        var status = TokenBudget.Assess(oversized, budget: 1000);
        Assert.True(status.OverBudget);
        Assert.True(status.UsingCondensed);

        // No condensed version yet: over budget but the fallback isn't available.
        oversized[0].CondensedText = null;
        oversized[0].CondensedTokenCount = null;
        status = TokenBudget.Assess(oversized, budget: 1000);
        Assert.True(status.OverBudget);
        Assert.False(status.UsingCondensed);
    }

    [Fact]
    public void Condensed_context_uses_condensed_text_and_falls_back_per_document()
    {
        var documents = new List<Document>
        {
            new() { Name = "big.pdf", Kind = DocumentKind.Upload, ExtractedText = "FULL-TEXT", CondensedText = "CONDENSED-TEXT" },
            new() { Name = "small.pdf", Kind = DocumentKind.Upload, ExtractedText = "OTHER-FULL" },
        };

        var condensed = WorkingContextBuilder.Build(WorkingContextKind.SourceMaterial, documents, [],
            useCondensedDocuments: true);
        Assert.Contains("CONDENSED-TEXT", condensed);
        Assert.DoesNotContain("FULL-TEXT", condensed);
        Assert.Contains("OTHER-FULL", condensed); // No condensed version: full text is used.

        var full = WorkingContextBuilder.Build(WorkingContextKind.SourceMaterial, documents, []);
        Assert.Contains("FULL-TEXT", full);
        Assert.DoesNotContain("CONDENSED-TEXT", full);
    }
}
