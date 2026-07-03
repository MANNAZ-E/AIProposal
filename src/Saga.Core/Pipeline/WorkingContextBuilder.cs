using System.Text;
using Saga.Core.Domain;

namespace Saga.Core.Pipeline;

/// <summary>
/// Assembles the material the AI is allowed to see (spec §18). Used identically by chat and
/// generation. Source priority: client documents > user notes > proposal artifacts, and the
/// client profile must never override the client's own documents.
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
        ArtifactType? excludeArtifact = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<client_documents>");
        sb.AppendLine("These are the client's own documents. They are the highest-priority source and always take precedence over notes, research and generated artifacts.");
        foreach (var doc in documents.Where(d => d.Kind == DocumentKind.Upload))
        {
            sb.AppendLine($"<document name=\"{doc.Name}\">");
            sb.AppendLine(doc.ExtractedText);
            sb.AppendLine("</document>");
        }
        sb.AppendLine("</client_documents>");

        sb.AppendLine("<user_notes>");
        sb.AppendLine("Notes written by the Mannaz consultant. Second-priority source.");
        foreach (var note in documents.Where(d => d.Kind == DocumentKind.Note))
        {
            sb.AppendLine($"<note title=\"{note.Name}\">");
            sb.AppendLine(note.ExtractedText);
            sb.AppendLine("</note>");
        }
        sb.AppendLine("</user_notes>");

        if (kind == WorkingContextKind.SourceMaterial)
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

    /// <summary>Which context each artifact type is generated from.</summary>
    public static WorkingContextKind ContextFor(ArtifactType type) => type switch
    {
        ArtifactType.ClientProfile or ArtifactType.Summary or ArtifactType.Requirements
            => WorkingContextKind.SourceMaterial,
        ArtifactType.Scoping => WorkingContextKind.Analysis,
        _ => WorkingContextKind.FullProject,
    };
}
