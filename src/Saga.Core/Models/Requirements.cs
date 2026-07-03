using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

public enum RequirementType
{
    Mandatory = 0,
    Criterion = 1,
    Wish = 2,
    Practical = 3,
    Unclear = 4,
}

public enum RequirementStatus
{
    NotAddressed = 0,
    Partly = 1,
    Addressed = 2,
}

/// <summary>One row in the requirements and criteria list (spec §12).</summary>
public class RequirementItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The client's wording, as close to verbatim as possible.</summary>
    public string Text { get; set; } = "";

    public string? SourceDocument { get; set; }

    /// <summary>Page or section reference within the source document.</summary>
    public string? SourceLocation { get; set; }

    public RequirementType Type { get; set; }

    /// <summary>AI's interpretation of what the requirement actually demands.</summary>
    public string? Interpretation { get; set; }

    /// <summary>How the proposal should address it.</summary>
    public string? HowAddressed { get; set; }

    public RequirementStatus Status { get; set; } = RequirementStatus.NotAddressed;

    /// <summary>True when a user added this row manually rather than AI extracting it.</summary>
    public bool UserAdded { get; set; }
}

/// <summary>Payload stored in the Requirements artifact's ContentJson.</summary>
public class RequirementsPayload
{
    public List<RequirementItem> Items { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RequirementsPayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new RequirementsPayload()
            : JsonSerializer.Deserialize<RequirementsPayload>(json, Options) ?? new RequirementsPayload();
}
