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
        var sb = new StringBuilder(BuildSystemPromptCore(proposal, voice));
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

    private static string BuildSystemPromptCore(Proposal proposal, MannazVoiceSettings voice)
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

        ArtifactType.ClientProfile => """
            Write a short, research-based profile of the client (about half a page of Markdown, no top-level title).
            Cover:
            - **The client** — who they are, size, sector position.
            - **Industry and market** — the landscape and pressures they operate in.
            - **Business model** — how they create value.
            - **Strategic themes** — their stated priorities and direction.
            - **Relevant initiatives** — programmes, transformations or investments that relate to this assignment.
            - **Contextual factors** — anything (regulation, ownership, market events) that may matter for the proposal.
            If web research results are provided in the context, ground the profile in them and keep any source citations.
            Otherwise rely on the client material and general knowledge, and add this caveat as the final line, in the
            same language as the profile: "*This profile is based on general knowledge and the uploaded material; verify current facts.*"
            This profile is background research: it must never contradict or override the client's own documents.
            """,

        ArtifactType.Scoping => """
            Write the scoping of the assignment as Markdown. It must be professionally oriented — an analysis of the
            client's situation, not merely a practical boundary. Use exactly these four sections (translate the
            headings into the language you are writing in):

            ## Client situation and context
            The client's current situation and the organizational, strategic or operational context the assignment belongs to.

            ## Client business challenge
            The problem, pressure or need the client is trying to solve.

            ## Client aspiration and desired future state
            What the client wants to achieve; the effect or change they are aiming for.

            ## Boundaries and assumptions
            What is included, what is not included, and the assumptions this scoping relies on.
            """,

        ArtifactType.SolutionProposal => """
            Write Mannaz's proposed solution as Markdown — the red thread that will inform the proposal's structure
            and content. Cover, with a section for each:
            - **Overall solution idea** — the core concept in a few sentences.
            - **Professional approach** — the consulting stance and principles behind the solution.
            - **Key methods** — the specific methods and frameworks to be used and why they fit.
            - **Process** — phases or steps, with a clear logic from start to effect.
            - **Deliverables** — what the client concretely receives.
            - **How the solution addresses the client's requirements** — connect explicitly to the requirements and criteria list.
            Be concrete: this is the professional heart of the proposal.
            """,

        ArtifactType.Structure when format == OutputFormat.PowerPoint => """
            Design the slide structure for the PowerPoint proposal: sequence, storyline, key messages and visual flow.
            Base it on the scoping, the solution proposal, and the requirements and criteria list.

            Return ONLY a JSON array (no markdown fences, no commentary). One element per slide, in presentation order:
            {
              "title": "slide title (in the proposal's language)",
              "purpose": "what this slide is for in the storyline",
              "keyMessage": "the one thing the audience must take away",
              "estimatedLength": "e.g. '1 slide'",
              "visualSuggestion": "suggested visual treatment (diagram, timeline, photo, table...)",
              "addresses": "which requirements or themes this slide addresses"
            }
            Typically 10–16 slides: opening, understanding of the client's situation, approach/solution,
            process, deliverables, why Mannaz, practical matters, closing.
            """,

        ArtifactType.Structure => """
            Design the chapter structure for the Word proposal: chapters, argumentation and written narrative.
            Base it on the scoping, the solution proposal, and the requirements and criteria list.

            Return ONLY a JSON array (no markdown fences, no commentary). One element per chapter, in document order:
            {
              "title": "chapter heading (in the proposal's language)",
              "purpose": "what this chapter is for in the argument",
              "keyMessage": "the central points of the chapter",
              "estimatedLength": "recommended length, e.g. '2-3 pages'",
              "visualSuggestion": null,
              "addresses": "which requirements or themes this chapter addresses"
            }
            """,

        _ => throw new NotSupportedException($"No prompt defined yet for {type}."),
    };

    /// <summary>System prompt for generating one content unit (a slide or section) from the structure.</summary>
    public static string BuildContentUnitPrompt(Proposal proposal, MannazVoiceSettings voice,
        Models.StructureItem item, int position, int total, string? instruction = null)
    {
        var basePrompt = BuildSystemPromptCore(proposal, voice);
        var unitKind = proposal.OutputFormat == OutputFormat.PowerPoint ? "slide" : "section";
        var bodyRule = proposal.OutputFormat == OutputFormat.PowerPoint
            ? "Write the slide body as concise bullet points and short statements (markdown), suitable for a slide — not paragraphs of prose."
            : "Write the section as coherent, well-argued document prose in markdown.";

        var task = $"""
            ## Task
            Write {unitKind} {position} of {total} of the proposal, following the approved structure.

            This {unitKind}:
            - Title: {item.Title}
            - Purpose: {item.Purpose}
            - Key message: {item.KeyMessage}
            - Estimated length: {item.EstimatedLength}
            - Addresses: {item.Addresses}

            {bodyRule}
            Return ONLY the body content as markdown — no title heading (the title is stored separately), no commentary.
            Stay strictly on this {unitKind}'s purpose; other {unitKind}s cover the rest of the storyline.
            """;

        var result = basePrompt + task;
        if (!string.IsNullOrWhiteSpace(instruction))
            result += $"\n\n## Additional instruction from the consultant\n{instruction.Trim()}";
        return result;
    }
}
