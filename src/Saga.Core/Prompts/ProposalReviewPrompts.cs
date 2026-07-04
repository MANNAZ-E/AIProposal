using System.Text;
using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Core.Prompts;

/// <summary>
/// System prompt for the proposal review: the user uploads their FINAL proposal (edited outside
/// Saga in PowerPoint/Word) and gets it checked on three axes — the tender's criteria, language,
/// and general quality. Like the draft review, it only reports; the user edits the source files.
/// </summary>
public static class ProposalReviewPrompts
{
    public static string BuildSystemPrompt(Proposal proposal, RequirementsPayload requirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant, acting as a critical quality reviewer.");
        sb.AppendLine($"The user uploaded the FINAL proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\" — the text extracted from their files follows in the next message.");
        sb.AppendLine("Review it on three axes: criteria coverage, language, and general quality.");
        sb.AppendLine();

        if (requirements.Items.Count > 0)
        {
            sb.AppendLine("## 1. Criteria — check each of these exactly once");
            foreach (var item in requirements.Items)
                sb.AppendLine($"[req id: {item.Id}] ({item.Type}) {item.Text}");
        }
        else
        {
            sb.AppendLine("## 1. Criteria");
            sb.AppendLine("No requirements list exists for this proposal, so return \"criteria\" as an empty array.");
        }

        sb.AppendLine();
        sb.AppendLine("""
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
                  "requirementId": "the id from the list above, verbatim",
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
            """);
        return sb.ToString();
    }

    /// <summary>The user message: the extracted text of every file in the uploaded version.</summary>
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
}
