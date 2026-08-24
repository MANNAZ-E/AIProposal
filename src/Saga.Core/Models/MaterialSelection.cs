using System.Text.Json;
using Saga.Core.Domain;

namespace Saga.Core.Models;

/// <summary>
/// The material one chat is allowed to read, chosen when the chat starts and frozen from then on.
/// Stored as JSON on the chat rather than in join tables: nothing queries into it — it exists to
/// show what a chat can see and to seed a follow-up chat with the same choice.
/// </summary>
public record MaterialSelection(IReadOnlyList<Guid> DocumentIds, IReadOnlyList<ArtifactType> ArtifactTypes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly MaterialSelection Empty = new([], []);

    public bool IsEmpty => DocumentIds.Count == 0 && ArtifactTypes.Count == 0;

    /// <summary>Everything the proposal currently has — the default for a new chat.</summary>
    public static MaterialSelection Everything(
        IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
        => new(
            documents.Select(d => d.Id).ToList(),
            artifacts.Where(a => a.Status != ArtifactStatus.Empty).Select(a => a.Type).ToList());

    /// <summary>What a preset picks, so the presets stay a shortcut into the same selection.</summary>
    public static MaterialSelection ForPreset(
        WorkingContextKind kind, IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
    {
        var everything = Everything(documents, artifacts);
        return kind switch
        {
            WorkingContextKind.SourceMaterial => everything with { ArtifactTypes = [] },
            WorkingContextKind.Analysis => everything with
            {
                ArtifactTypes = everything.ArtifactTypes.Where(IsAnalysisType).ToList(),
            },
            _ => everything,
        };
    }

    /// <summary>The preset this selection matches, or <see cref="WorkingContextKind.Custom"/>.</summary>
    public WorkingContextKind PresetOrCustom(
        IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
    {
        foreach (var preset in (WorkingContextKind[])
                 [WorkingContextKind.SourceMaterial, WorkingContextKind.Analysis, WorkingContextKind.FullProject])
        {
            var candidate = ForPreset(preset, documents, artifacts);
            if (candidate.DocumentIds.ToHashSet().SetEquals(DocumentIds)
                && candidate.ArtifactTypes.ToHashSet().SetEquals(ArtifactTypes))
                return preset;
        }
        return WorkingContextKind.Custom;
    }

    private static bool IsAnalysisType(ArtifactType type)
        => type is ArtifactType.ClientProfile or ArtifactType.Summary or ArtifactType.Requirements;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>An unreadable or absent value reads as "nothing frozen yet", not as a crash.</summary>
    public static MaterialSelection? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MaterialSelection>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
