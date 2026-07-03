using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

/// <summary>
/// One content unit — a slide (PowerPoint) or section (Word). Individually editable,
/// lockable and regenerable (spec §16 + per-unit granularity decision).
/// </summary>
public class ContentUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The structure item this unit implements.</summary>
    public Guid StructureItemId { get; set; }

    public string Title { get; set; } = "";

    public string? KeyMessage { get; set; }

    /// <summary>Slide body (bullets/short text) or section prose, as markdown.</summary>
    public string BodyMarkdown { get; set; } = "";

    /// <summary>Locked units are never overwritten by whole-content regeneration.</summary>
    public bool IsLocked { get; set; }
}

/// <summary>Payload stored in the Content artifact's ContentJson.</summary>
public class ContentPayload
{
    public List<ContentUnit> Units { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ContentPayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new ContentPayload()
            : JsonSerializer.Deserialize<ContentPayload>(json, Options) ?? new ContentPayload();
}
