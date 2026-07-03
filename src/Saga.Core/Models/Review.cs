using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

public enum ReviewCoverage
{
    NotAddressed = 0,
    Partly = 1,
    Addressed = 2,
}

/// <summary>Coverage verdict for one requirement (spec §16). The review never changes the proposal.</summary>
public class ReviewItem
{
    public Guid RequirementId { get; set; }

    /// <summary>Snapshot of the requirement's text at review time.</summary>
    public string RequirementText { get; set; } = "";

    public RequirementType RequirementType { get; set; }

    public ReviewCoverage Coverage { get; set; }

    /// <summary>Where in the proposal it is addressed (slide/section titles).</summary>
    public string? WhereAddressed { get; set; }

    /// <summary>Suggested improvement, if any.</summary>
    public string? Improvement { get; set; }

    /// <summary>Risk if left as is.</summary>
    public string? Risk { get; set; }
}

/// <summary>Payload stored in the Review artifact's ContentJson.</summary>
public class ReviewPayload
{
    public List<ReviewItem> Items { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ReviewPayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new ReviewPayload()
            : JsonSerializer.Deserialize<ReviewPayload>(json, Options) ?? new ReviewPayload();
}
