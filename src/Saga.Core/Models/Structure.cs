using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

/// <summary>
/// One slide (PowerPoint) or section (Word) in the proposal structure (spec §15).
/// The length is kept per format, so switching between PowerPoint and Word preserves
/// both the slide count and the word count alongside the shared title/purpose/key message.
/// </summary>
public class StructureItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Slide title or section heading.</summary>
    public string Title { get; set; } = "";

    /// <summary>What this slide/section is for.</summary>
    public string? Purpose { get; set; }

    /// <summary>The key message (PowerPoint) or central points (Word).</summary>
    public string? KeyMessage { get; set; }

    /// <summary>How many slides this item covers (PowerPoint).</summary>
    public int? SlideCount { get; set; }

    /// <summary>Target length in words (Word).</summary>
    public int? WordCount { get; set; }
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
