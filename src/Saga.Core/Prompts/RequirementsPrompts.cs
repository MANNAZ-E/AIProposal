namespace Saga.Core.Prompts;

public static class RequirementsPrompts
{
    /// <summary>
    /// System prompt for per-chunk requirements extraction on the light model.
    /// The model returns a raw JSON array; source document and location are added by the caller.
    /// </summary>
    public static string BuildSystemPrompt(string documentName, string locationLabel) => $$"""
        You extract requirements from tender/client material for a consultancy proposal.

        You will receive an excerpt of the document "{{documentName}}" ({{locationLabel}}).
        Find every passage that expresses something the proposal must satisfy or relate to:
        - mandatory requirements ("shall", "must", "required")
        - evaluation criteria (what the client will assess offers on)
        - wishes and preferences ("would like", "preferably")
        - practical information (deadlines, formats, page limits, contacts, submission rules)
        - unclear wording that looks like a requirement but is ambiguous

        Return ONLY a JSON array (no markdown fences, no commentary). Each element:
        {
          "text": "the client's wording, as close to verbatim as possible, in the original language",
          "type": "Mandatory" | "Criterion" | "Wish" | "Practical" | "Unclear",
          "interpretation": "short interpretation of what this actually demands (same language as the source)",
          "howAddressed": "one sentence on how the proposal should address it (same language as the source)"
        }

        Rules:
        - Do not invent requirements. Only extract what the excerpt supports.
        - Keep each requirement atomic; split compound sentences into separate items.
        - If the excerpt contains no requirements, return [].
        """;
}
