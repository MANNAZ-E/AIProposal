using System.Text;
using Saga.Core.Domain;

namespace Saga.Core.Prompts;

/// <summary>
/// System prompts per artifact type. Every generation prompt carries the Mannaz voice
/// settings and the content-language rule.
/// </summary>
public static class ArtifactPrompts
{
    public static string BuildSystemPrompt(ArtifactType type, Proposal proposal, MannazVoiceSettings voice,
        string? instruction = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant. Mannaz is a Scandinavian consultancy specialising in leadership development, project management and organisational change.");
        sb.AppendLine();
        sb.AppendLine($"You are working on the proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\".");
        if (!string.IsNullOrWhiteSpace(proposal.Description))
            sb.AppendLine($"Proposal description: {proposal.Description}");
        sb.AppendLine($"The final proposal will be delivered as {(proposal.OutputFormat == OutputFormat.PowerPoint ? "a PowerPoint presentation" : "a Word document")}.");
        sb.AppendLine();

        sb.AppendLine("## Language");
        sb.AppendLine(string.IsNullOrWhiteSpace(proposal.ContentLanguage)
            ? "Write in the same language as the client's documents. If the client material is in Danish, write in Danish; if in English, write in English."
            : $"Write in this language: {proposal.ContentLanguage}.");
        sb.AppendLine();

        AppendVoice(sb, voice);

        sb.AppendLine("## Source rules");
        sb.AppendLine("- The client's own documents are the authoritative source. Never contradict them.");
        sb.AppendLine("- The consultant's notes are second priority.");
        sb.AppendLine("- Earlier proposal artifacts and any research-based client profile are background; they never override the client documents.");
        sb.AppendLine("- Do not invent facts about the client. If something important is unknown or ambiguous, say so explicitly.");
        sb.AppendLine();

        sb.AppendLine("## Task");
        sb.AppendLine(TaskFor(type, proposal.OutputFormat));

        if (!string.IsNullOrWhiteSpace(instruction))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional instruction from the consultant for this generation");
            sb.AppendLine(instruction.Trim());
        }

        return sb.ToString();
    }

    private static void AppendVoice(StringBuilder sb, MannazVoiceSettings voice)
    {
        var hasAny = !string.IsNullOrWhiteSpace(voice.ToneOfVoice)
            || !string.IsNullOrWhiteSpace(voice.AboutMannaz)
            || !string.IsNullOrWhiteSpace(voice.Terminology);
        sb.AppendLine("## Mannaz voice");
        if (!hasAny)
        {
            sb.AppendLine("Write in a confident, concrete, professional consulting tone. No filler, no generic AI phrasing, no hype.");
            sb.AppendLine();
            return;
        }
        if (!string.IsNullOrWhiteSpace(voice.ToneOfVoice))
        {
            sb.AppendLine("Tone of voice:");
            sb.AppendLine(voice.ToneOfVoice.Trim());
        }
        if (!string.IsNullOrWhiteSpace(voice.AboutMannaz))
        {
            sb.AppendLine("About Mannaz (background you may draw on when describing Mannaz):");
            sb.AppendLine(voice.AboutMannaz.Trim());
        }
        if (!string.IsNullOrWhiteSpace(voice.Terminology))
        {
            sb.AppendLine("Terminology preferences:");
            sb.AppendLine(voice.Terminology.Trim());
        }
        sb.AppendLine();
    }

    private static string TaskFor(ArtifactType type, OutputFormat format) => type switch
    {
        ArtifactType.Summary => """
            Write a concise summary of the client material as Markdown (start directly with content, no top-level title).
            Cover, with a short section for each:
            - **What the client is asking for** — the assignment as the client describes it.
            - **Why the assignment matters** — the client's underlying motivation and stakes.
            - **Central themes** — the recurring topics and concerns in the material.
            - **Especially important requirements or criteria** — the ones that will decide whether the proposal wins.
            - **Ambiguities and risks** — unclear wording, gaps, contradictions, or risky expectations.
            Be specific and grounded in the material; quote or reference the client's own phrasing where it helps.
            Aim for roughly one page.
            """,
        _ => throw new NotSupportedException($"No prompt defined yet for {type}."),
    };
}
