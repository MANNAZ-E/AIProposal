namespace Saga.Core.Domain;

/// <summary>Append-only snapshot of an artifact, written on every save, generation, and restore.</summary>
public class ArtifactVersion
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public Artifact? Artifact { get; set; }

    public string? ContentMarkdown { get; set; }
    public string? ContentJson { get; set; }
    public VersionOrigin Origin { get; set; }

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
