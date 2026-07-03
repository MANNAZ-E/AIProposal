using System.Text;
using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Core.Prompts;

/// <summary>
/// System prompt for the click-to-run review (spec §16): check the proposal against the
/// current requirements list and report coverage — never change the proposal.
/// </summary>
public static class ReviewPrompts
{
    public static string BuildSystemPrompt(Proposal proposal, RequirementsPayload requirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant, acting as a critical quality reviewer.");
        sb.AppendLine($"Review the current proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\" against the requirements list below.");
        sb.AppendLine();
        sb.AppendLine("## Requirements to check (review each one exactly once)");
        foreach (var item in requirements.Items)
            sb.AppendLine($"[req id: {item.Id}] ({item.Type}) {item.Text}");
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine("""
            Judge each requirement against the proposal's structure and content in the context — not against
            good intentions in the analysis artifacts. Base "whereAddressed" on the actual slide/section titles.

            Return ONLY a JSON array (no markdown fences, no commentary), one element per requirement above:
            {
              "requirementId": "the id from the list above, verbatim",
              "coverage": "Addressed" | "Partly" | "NotAddressed",
              "whereAddressed": "which slides/sections address it, or null",
              "improvement": "concrete suggestion to strengthen coverage, or null if none needed",
              "risk": "the risk to the bid if left as is, or null"
            }
            Write whereAddressed/improvement/risk in the same language as the proposal content.
            Be honest: a requirement mentioned in passing is "Partly", not "Addressed".
            """);
        return sb.ToString();
    }
}
