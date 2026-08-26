using System.Text;
using Saga.Core.Domain;

namespace Saga.Core.Prompts;

/// <summary>
/// System prompt for the proposal chat (spec: Q&amp;A only in v1 — the chat answers questions
/// about the selected working context; it never modifies the proposal).
/// </summary>
public static class ChatPrompts
{
    /// <param name="hasMaterial">
    /// False when the chat was started with nothing picked. There is then no context block to
    /// answer strictly from, so the grounding rules would only tell the model to refuse every
    /// question; it works from the conversation and its own knowledge instead.
    /// </param>
    public static string BuildSystemPrompt(
        Proposal proposal, WorkingContextKind kind, bool hasMaterial = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Saga, Mannaz's proposal assistant. Mannaz is a Scandinavian consultancy specialising in leadership development, project management and organisational change.");
        sb.AppendLine();
        sb.AppendLine($"A Mannaz consultant is asking questions about the proposal \"{proposal.Title}\" for the client \"{proposal.ClientName}\".");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        if (hasMaterial)
        {
            sb.AppendLine($"- Answer strictly from the provided context ({Describe(kind)}). Do not use outside knowledge about the client.");
            sb.AppendLine("- The client's own documents are the authoritative source; the consultant's notes are second priority; generated artifacts are background only.");
            sb.AppendLine("- If the answer is not in the context, say so plainly instead of guessing.");
            sb.AppendLine("- When you cite a fact, mention which document or artifact it comes from.");
        }
        else
        {
            sb.AppendLine("- No material from this proposal has been given to you. Answer from the conversation itself and from your general expertise in consulting, bid writing and this proposal's subject.");
            sb.AppendLine("- Say nothing about this client as fact: you have not read their documents. If a question needs them, say which material would answer it.");
        }
        sb.AppendLine("- Answer in the language the question was asked in.");
        sb.AppendLine("- You only answer questions. You cannot change the proposal; if asked to, explain that edits happen in the artifact views.");
        return sb.ToString();
    }

    private static string Describe(WorkingContextKind kind) => kind switch
    {
        WorkingContextKind.ClientMaterial => "the material the client supplied",
        WorkingContextKind.SourceMaterial => "the client's documents and the consultant's notes",
        WorkingContextKind.Analysis => "the source material plus the client profile, summary and requirements",
        // A hand-picked selection: the context block below is the authoritative list, so the
        // prompt points at it instead of naming sources that may not be in it.
        WorkingContextKind.Custom => "the material picked for this chat, shown in full below",
        _ => "the full project: source material and all proposal artifacts",
    };
}
