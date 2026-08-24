using System.Text;
using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Core.Prompts;

/// <summary>
/// Prompts for the proposal review: the user uploads their FINAL proposal (edited outside Saga in
/// PowerPoint/Word) and gets it checked on three axes — the tender's criteria, language, and general
/// quality. Like the draft review, it only reports; the user edits the source files.
///
/// Split into system prompt, material and instruction so re-running the review on a version replays
/// the same prefix: the criteria list and the extracted files come first, the three-axis task and the
/// output contract last.
/// </summary>
public static class ProposalReviewPrompts
{
    /// <summary>The stable prefix: the reviewer's role and which proposal was uploaded.</summary>
    public static string BuildSystemPrompt(Proposal proposal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant, acting as a critical quality reviewer.");
        sb.AppendLine($"The user uploaded the FINAL proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\" — the criteria to check and the text extracted from their files follow in the next messages.");
        sb.AppendLine("Review it on three axes: criteria coverage, language, and general quality.");
        return sb.ToString();
    }

    /// <summary>The criteria list, as the first material message.</summary>
    public static string BuildCriteriaMessage(RequirementsPayload requirements)
    {
        var sb = new StringBuilder();
        if (requirements.Items.Count > 0)
        {
            sb.AppendLine("## Criteria — check each of these exactly once");
            foreach (var item in requirements.Items)
                sb.AppendLine($"[req id: {item.Id}] ({item.Type}) {item.Text}");
        }
        else
        {
            sb.AppendLine("## Criteria");
            sb.AppendLine("No requirements list exists for this proposal, so return \"criteria\" as an empty array.");
        }
        return sb.ToString();
    }

    /// <summary>The second material message: the extracted text of every file in the uploaded version.</summary>
    public static string BuildFilesContext(IEnumerable<FinalProposalFile> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            sb.AppendLine($"===== FILE: {file.Name} =====");
            sb.AppendLine(file.ExtractedText);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The trailing instruction: the three axes and the exact JSON to return.</summary>
    public const string Instruction = """
        ## 1. Criteria
        Judge the uploaded proposal against each criterion in the list above, exactly once.

        ## 2. Language
        Find spelling errors, grammatical mistakes, and weird or clumsy phrases. Quote the exact
        text, say what is wrong, and give the corrected wording. Report real findings only — do
        not invent issues to fill a quota.

        ## 3. General quality
        The primary improvements you can find: argumentation, structure, concreteness, client
        focus, consistency. For each, give a couple of alternative suggestions on how to edit
        ("suggestions") and the single edit you recommend ("recommendedEdit").

        ## Output
        Return ONLY a JSON object (no markdown fences, no commentary):
        {
          "criteria": [
            {
              "requirementId": "the id from the criteria list above, verbatim",
              "coverage": "Addressed" | "Partly" | "NotAddressed",
              "whereAddressed": "which files/slides/sections address it, or null",
              "improvement": "concrete suggestion to strengthen coverage, or null if none needed",
              "risk": "the risk to the bid if left as is, or null"
            }
          ],
          "language": [
            { "where": "file/slide/section", "quote": "the problematic text", "issue": "what is wrong", "suggestion": "the corrected wording" }
          ],
          "quality": [
            { "where": "file/slide/section", "observation": "what could be better and why", "suggestions": ["edit option 1", "edit option 2"], "recommendedEdit": "the edit you recommend" }
          ]
        }
        Write everything except the ids in the same language as the proposal.
        Be honest: a requirement mentioned in passing is "Partly", not "Addressed".
        """;
}
