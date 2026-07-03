using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

/// <summary>
/// One slide (PowerPoint) or chapter (Word) in the proposal structure (spec §15).
/// </summary>
public class StructureItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Slide title or chapter heading.</summary>
    public string Title { get; set; } = "";

    /// <summary>What this slide/chapter is for.</summary>
    public string? Purpose { get; set; }

    /// <summary>The key message (PowerPoint) or central points (Word).</summary>
    public string? KeyMessage { get; set; }

    /// <summary>Estimated length, e.g. "1 slide" or "2–3 pages".</summary>
    public string? EstimatedLength { get; set; }

    /// <summary>Visual suggestion (PowerPoint only).</summary>
    public string? VisualSuggestion { get; set; }

    /// <summary>Which requirements or themes this addresses.</summary>
    public string? Addresses { get; set; }
}

/// <summary>Payload stored in the Structure artifact's ContentJson.</summary>
public class StructurePayload
{
    public List<StructureItem> Items { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static StructurePayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new StructurePayload()
            : JsonSerializer.Deserialize<StructurePayload>(json, Options) ?? new StructurePayload();
}
