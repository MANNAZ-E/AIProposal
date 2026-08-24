using System.Text;
using Saga.Core.Domain;

namespace Saga.Core.Pipeline;

/// <summary>
/// Assembles the material the AI is allowed to see (spec §18). Used identically by chat and
/// generation. Source priority: uploaded documents > user notes > proposal artifacts, and the
/// client profile must never override the client's own documents. Within the documents, the
/// proposal's document types rank against each other in their own order.
/// </summary>
public static class WorkingContextBuilder
{
    /// <summary>Artifact types visible in the Analysis context (beyond source material).</summary>
    private static readonly ArtifactType[] AnalysisTypes =
        [ArtifactType.ClientProfile, ArtifactType.Summary, ArtifactType.Requirements];

    public static string Build(
        WorkingContextKind kind,
        IReadOnlyList<Document> documents,
        IReadOnlyList<Artifact> artifacts,
        ArtifactType? excludeArtifact = null,
        bool useCondensedDocuments = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<source_documents>");
        sb.AppendLine("Documents uploaded to this proposal, filed by category. The categories below are in priority order: where two documents conflict, the one from the earlier category wins. Uploaded documents are the highest-priority source and always take precedence over notes, research and generated artifacts.");
        if (useCondensedDocuments)
            sb.AppendLine("Note: the documents below are AI-condensed versions of larger originals.");
        foreach (var category in Categorise(documents.Where(d => d.Kind == DocumentKind.Upload)))
        {
            sb.AppendLine($"<category name=\"{category.Key}\">");
            foreach (var doc in category)
            {
                sb.AppendLine($"<document name=\"{doc.Name}\">");
                sb.AppendLine(useCondensedDocuments ? doc.CondensedText ?? doc.ExtractedText : doc.ExtractedText);
                sb.AppendLine("</document>");
            }
            sb.AppendLine("</category>");
        }
        sb.AppendLine("</source_documents>");

        sb.AppendLine("<user_notes>");
        sb.AppendLine("Notes written by the Mannaz consultant, each filed under the same categories as the documents above. Second-priority source.");
        foreach (var note in documents.Where(d => d.Kind == DocumentKind.Note))
        {
            sb.AppendLine($"<note title=\"{note.Name}\" category=\"{CategoryName(note)}\">");
            sb.AppendLine(note.ExtractedText);
            sb.AppendLine("</note>");
        }
        sb.AppendLine("</user_notes>");

        // Chat narrows by its own selection before it gets here, so the client-material kind only
        // reaches this far as a label - but it is a documents-only context either way.
        if (kind is WorkingContextKind.SourceMaterial or WorkingContextKind.ClientMaterial)
            return sb.ToString();

        var visibleTypes = kind == WorkingContextKind.Analysis
            ? AnalysisTypes
            : Enum.GetValues<ArtifactType>();

        sb.AppendLine("<proposal_artifacts>");
        sb.AppendLine("Artifacts generated or edited earlier in this proposal. Background only — where they conflict with the client documents, the client documents win. The client profile is research-based and must never override the client's own documents.");
        foreach (var artifact in artifacts
                     .Where(a => a.Status != ArtifactStatus.Empty && visibleTypes.Contains(a.Type)
                                 && a.Type != excludeArtifact)
                     .OrderBy(a => a.Type))
        {
            var body = artifact.ContentMarkdown ?? artifact.ContentJson ?? "";
            if (string.IsNullOrWhiteSpace(body)) continue;
            sb.AppendLine($"<artifact type=\"{artifact.Type}\">");
            sb.AppendLine(body);
            sb.AppendLine("</artifact>");
        }
        sb.AppendLine("</proposal_artifacts>");

        return sb.ToString();
    }

    /// <summary>
    /// Groups material by its document type, in the type's own order — which is the priority
    /// order the prompt above tells the model to resolve conflicts by.
    /// </summary>
    private static IEnumerable<IGrouping<string, Document>> Categorise(IEnumerable<Document> documents)
        => documents
            .GroupBy(CategoryName)
            .OrderBy(g => g.Min(d => d.DocumentType?.SortOrder ?? int.MaxValue))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The type is always set in the app; the fallback only covers documents built without the
    /// navigation loaded (unit tests), so the material still reaches the model.
    /// </summary>
    private static string CategoryName(Document document) => document.DocumentType?.Name ?? "Material";

    /// <summary>Which context each artifact type is generated from.</summary>
    public static WorkingContextKind ContextFor(ArtifactType type) => type switch
    {
        ArtifactType.ClientProfile or ArtifactType.Summary or ArtifactType.Requirements
            => WorkingContextKind.SourceMaterial,
        ArtifactType.Scoping => WorkingContextKind.Analysis,
        _ => WorkingContextKind.FullProject,
    };
}
