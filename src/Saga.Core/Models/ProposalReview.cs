using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saga.Core.Models;

/// <summary>A language finding in the uploaded final proposal: spelling, grammar, odd phrasing.</summary>
public class LanguageFinding
{
    /// <summary>Where in the uploaded files (file / slide / section).</summary>
    public string? Where { get; set; }

    /// <summary>The problematic text, quoted.</summary>
    public string? Quote { get; set; }

    /// <summary>What is wrong with it.</summary>
    public string? Issue { get; set; }

    /// <summary>The corrected wording.</summary>
    public string? Suggestion { get; set; }
}

/// <summary>A general-quality finding: the primary improvements the review sees.</summary>
public class QualityFinding
{
    public string? Where { get; set; }

    /// <summary>What could be better and why it matters.</summary>
    public string? Observation { get; set; }

    /// <summary>A couple of alternative ways to edit.</summary>
    public List<string> Suggestions { get; set; } = [];

    /// <summary>The single edit the review recommends.</summary>
    public string? RecommendedEdit { get; set; }
}

/// <summary>
/// Result of a proposal review (of the user's uploaded final proposal): criteria coverage
/// against the requirements list, language findings, and general-quality findings.
/// Stored on <c>FinalProposalVersion.ReviewJson</c>.
/// </summary>
public class ProposalReviewPayload
{
    public List<ReviewItem> Criteria { get; set; } = [];
    public List<LanguageFinding> Language { get; set; } = [];
    public List<QualityFinding> Quality { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ProposalReviewPayload FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new ProposalReviewPayload()
            : JsonSerializer.Deserialize<ProposalReviewPayload>(json, Options) ?? new ProposalReviewPayload();
}
