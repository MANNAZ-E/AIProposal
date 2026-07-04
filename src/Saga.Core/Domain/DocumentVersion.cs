namespace Saga.Core.Domain;

/// <summary>
/// Append-only snapshot of an uploaded document's extracted text, written on extraction,
/// every manual edit, and every restore — the same model as <see cref="ArtifactVersion"/>.
/// </summary>
public class DocumentVersion
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }

    public string Text { get; set; } = "";
    public VersionOrigin Origin { get; set; }

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
