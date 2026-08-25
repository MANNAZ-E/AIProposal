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

    /// <summary>
    /// The presets a chat can be started from, narrowest first — which is also the order
    /// <see cref="PresetOrCustom"/> resolves ties in, so a proposal holding nothing but client
    /// material reads as "Client materials" rather than as the wider preset selecting the same rows.
    /// The picker itself lists them broadest-first, so it reverses this order for display.
    /// </summary>
    public static readonly WorkingContextKind[] ChatPresets =
        [WorkingContextKind.ClientMaterial, WorkingContextKind.SourceMaterial, WorkingContextKind.FullProject];

    /// <summary>Broadest first, the reverse of <see cref="ChatPresets"/> — the order the picker
    /// lists the presets in, and the order <see cref="DefaultPreset"/> falls through them.</summary>
    public static readonly WorkingContextKind[] DisplayPresets = ChatPresets.Reverse().ToArray();

    /// <summary>
    /// Whether a preset picks anything beyond a narrower preset already on offer: "Client materials
    /// only" and "All materials" need matching documents to exist at all, and "Everything" needs
    /// artifacts — without them it would just select the same rows as "All materials". The picker
    /// greys out what is unavailable, and <see cref="DefaultPreset"/> skips it.
    /// </summary>
    public static bool IsPresetAvailable(
        WorkingContextKind kind, IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
        => kind switch
        {
            WorkingContextKind.ClientMaterial => documents.Any(IsClientMaterial),
            WorkingContextKind.SourceMaterial => documents.Any(),
            WorkingContextKind.FullProject => artifacts.Any(a => a.Status != ArtifactStatus.Empty),
            _ => true,
        };

    /// <summary>
    /// What a new chat starts on: the broadest preset that is actually available, so a proposal
    /// with no artifacts yet opens on "All materials" rather than on a greyed-out "Everything".
    /// <see cref="WorkingContextKind.Custom"/> when the proposal has nothing to read at all.
    /// </summary>
    public static WorkingContextKind DefaultPreset(
        IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
    {
        var material = documents as IReadOnlyCollection<Document> ?? documents.ToList();
        var built = artifacts as IReadOnlyCollection<Artifact> ?? artifacts.ToList();
        foreach (var preset in DisplayPresets)
            if (IsPresetAvailable(preset, material, built)) return preset;
        return WorkingContextKind.Custom;
    }

    /// <summary>What a preset picks, so the presets stay a shortcut into the same selection.</summary>
    public static MaterialSelection ForPreset(
        WorkingContextKind kind, IEnumerable<Document> documents, IEnumerable<Artifact> artifacts)
    {
        var everything = Everything(documents, artifacts);
        return kind switch
        {
            WorkingContextKind.ClientMaterial => new MaterialSelection(
                documents.Where(IsClientMaterial).Select(d => d.Id).ToList(), []),
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
        foreach (var preset in ChatPresets)
        {
            var candidate = ForPreset(preset, documents, artifacts);
            if (candidate.DocumentIds.ToHashSet().SetEquals(DocumentIds)
                && candidate.ArtifactTypes.ToHashSet().SetEquals(ArtifactTypes))
                return preset;
        }
        return WorkingContextKind.Custom;
    }

    /// <summary>
    /// Material filed under the fixed "Client materials" category. Matched by the type's name
    /// rather than an id, since the category is per-proposal but its name is not.
    /// </summary>
    private static bool IsClientMaterial(Document document)
        => string.Equals(document.DocumentType?.Name, DocumentType.ClientMaterialName,
            StringComparison.OrdinalIgnoreCase);

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
