using System.Text;
using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Core.Prompts;

/// <summary>
/// Prompts for the click-to-run review (spec §16): check the proposal against the current
/// requirements list and report coverage — never change the proposal. Split into system prompt,
/// material and instruction so a re-run replays the same prefix: the requirements list (which
/// rarely moves) comes first, then the working context, then the task and output contract last.
/// </summary>
public static class ReviewPrompts
{
    /// <summary>The stable prefix: the reviewer's role and which proposal is under review.</summary>
    public static string BuildSystemPrompt(Proposal proposal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant, acting as a critical quality reviewer.");
        sb.AppendLine($"Review the current proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\" against the requirements list you are given.");
        return sb.ToString();
    }

    /// <summary>The requirements list, as the first material message.</summary>
    public static string BuildRequirementsMessage(RequirementsPayload requirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Requirements to check (review each one exactly once)");
        foreach (var item in requirements.Items)
            sb.AppendLine($"[req id: {item.Id}] ({item.Type}) {item.Text}");
        return sb.ToString();
    }

    /// <summary>The trailing instruction: what to judge and the exact JSON to return.</summary>
    public const string Instruction = """
        ## Task
        Judge each requirement against the proposal's structure and content in the context above — not
        against good intentions in the analysis artifacts. Base "whereAddressed" on the actual
        slide/section titles.

        Return ONLY a JSON array (no markdown fences, no commentary), one element per requirement in
        the requirements list above:
        {
          "requirementId": "the id from that list, verbatim",
          "coverage": "Addressed" | "Partly" | "NotAddressed",
          "whereAddressed": "which slides/sections address it, or null",
          "improvement": "concrete suggestion to strengthen coverage, or null if none needed",
          "risk": "the risk to the bid if left as is, or null"
        }
        Write whereAddressed/improvement/risk in the same language as the proposal content.
        Be honest: a requirement mentioned in passing is "Partly", not "Addressed".
        """;
}
