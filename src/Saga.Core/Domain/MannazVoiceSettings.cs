namespace Saga.Core.Domain;

/// <summary>
/// Singleton, admin-editable settings injected into every generation prompt so output
/// sounds like Mannaz rather than generic AI prose.
/// </summary>
public class MannazVoiceSettings
{
    public Guid Id { get; set; }

    /// <summary>Tone-of-voice rules, e.g. "confident but not salesy, concrete, no filler".</summary>
    public string ToneOfVoice { get; set; } = "";

    /// <summary>Standard "About Mannaz" boilerplate available to generation.</summary>
    public string AboutMannaz { get; set; } = "";

    /// <summary>Preferred terminology and method vocabulary (and terms to avoid).</summary>
    public string Terminology { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
