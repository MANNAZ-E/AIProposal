using System.Runtime.CompilerServices;
using Saga.Core.Abstractions;

namespace Saga.Infrastructure.Ai;

/// <summary>
/// Stand-in used when AzureOpenAI:Endpoint is not configured, so the whole artifact loop
/// runs offline (and in tests) without spending tokens. Streams a canned markdown response.
/// </summary>
public class FakeAiService : IAiService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Structure prompts get a canned slide/section list back.
        if (request.SystemPrompt.Contains("\"keyMessage\""))
        {
            var isPowerPoint = request.SystemPrompt.Contains("\"slideCount\"");
            var length = isPowerPoint ? "\"slideCount\": 1" : "\"wordCount\": 400";
            var structureJson = $$"""
                [
                  {"title": "Understanding your situation", "purpose": "Show we understand the client's context", "keyMessage": "We know where you are and why this matters now", {{length}}},
                  {"title": "Our proposed approach", "purpose": "Present the solution idea", "keyMessage": "A phased, practical approach that fits your organisation", {{length}}},
                  {"title": "Deliverables and process", "purpose": "Make the offer concrete", "keyMessage": "Clear deliverables at every step", {{length}}}
                ]
                """;
            yield return new AiStreamEvent.Delta(structureJson);
            yield return new AiStreamEvent.Completed(800, 250, "fake-model");
            yield break;
        }

        // Proposal review prompts (checked before the draft review: both embed req ids, but only
        // this one asks for a recommendedEdit): canned three-axis JSON object with real ids.
        if (request.SystemPrompt.Contains("recommendedEdit"))
        {
            var reviewIds = System.Text.RegularExpressions.Regex
                .Matches(request.SystemPrompt, @"\[req id: ([0-9a-fA-F-]{36})\]")
                .Select(m => m.Groups[1].Value)
                .ToList();
            var criteriaCoverages = new[]
            {
                ("Addressed", "\"Our proposed approach\" (slide 3)", null as string, null as string),
                ("Partly", "\"Understanding your situation\" (slide 2)", "Add a concrete reference case with measurable results.", "The evaluators may score the criterion low without evidence."),
                ("NotAddressed", null, "Add a dedicated section covering this requirement.", "A mandatory requirement left unaddressed can disqualify the bid."),
            };
            var criteriaRows = reviewIds.Select((id, i) =>
            {
                var (coverage, where, improvement, risk) = criteriaCoverages[i % criteriaCoverages.Length];
                return $$"""
                    {"requirementId": "{{id}}", "coverage": "{{coverage}}", "whereAddressed": {{Json(where)}}, "improvement": {{Json(improvement)}}, "risk": {{Json(risk)}}}
                    """.Trim();
            });
            var proposalReviewJson = $$"""
                {
                  "criteria": [{{string.Join(",\n", criteriaRows)}}],
                  "language": [
                    {"where": "Slide 2", "quote": "we has extensive experience", "issue": "Subject–verb agreement error.", "suggestion": "we have extensive experience"},
                    {"where": "Slide 5", "quote": "leverage synergies going forward", "issue": "Empty consultant phrasing.", "suggestion": "combine the two programmes so participants share one learning path"}
                  ],
                  "quality": [
                    {"where": "Opening section", "observation": "The proposal opens with Mannaz's history instead of the client's challenge.", "suggestions": ["Open with a one-paragraph restatement of the client's aspiration.", "Move the company introduction to an appendix."], "recommendedEdit": "Open with a one-paragraph restatement of the client's aspiration, then link the approach directly to it."},
                    {"where": "Pricing section", "observation": "The price table is not connected to the deliverables.", "suggestions": ["Add a deliverable column to the price table.", "Reference deliverable numbers in each price row."], "recommendedEdit": "Add a deliverable column to the price table so every amount maps to a concrete output."}
                  ]
                }
                """;
            yield return new AiStreamEvent.Delta(proposalReviewJson);
            yield return new AiStreamEvent.Completed(1500, 400, "fake-model");
            yield break;
        }

        // Review prompts: echo the real requirement ids so the coverage report joins up offline.
        if (request.SystemPrompt.Contains("[req id: "))
        {
            var ids = System.Text.RegularExpressions.Regex
                .Matches(request.SystemPrompt, @"\[req id: ([0-9a-fA-F-]{36})\]")
                .Select(m => m.Groups[1].Value)
                .ToList();
            var coverages = new[]
            {
                ("Addressed", "\"Our proposed approach\" and \"Deliverables and process\"", null as string, null as string),
                ("Partly", "\"Understanding your situation\"", "Add a concrete reference case with measurable results.", "The evaluators may score the criterion low without evidence."),
                ("NotAddressed", null, "Add a dedicated slide covering this requirement.", "A mandatory requirement left unaddressed can disqualify the bid."),
            };
            var rows = ids.Select((id, i) =>
            {
                var (coverage, where, improvement, risk) = coverages[i % coverages.Length];
                return $$"""
                    {"requirementId": "{{id}}", "coverage": "{{coverage}}", "whereAddressed": {{Json(where)}}, "improvement": {{Json(improvement)}}, "risk": {{Json(risk)}}}
                    """.Trim();
            });
            yield return new AiStreamEvent.Delta("[" + string.Join(",\n", rows) + "]");
            yield return new AiStreamEvent.Completed(1200, 300, "fake-model");
            yield break;
        }

        // JSON-contract prompts (e.g. requirements extraction) get canned JSON back.
        if (request.SystemPrompt.Contains("Return ONLY a JSON array"))
        {
            var json = """
                [
                  {"text": "The offer must be submitted no later than the stated deadline.", "type": "Practical", "interpretation": "Hard submission deadline.", "howAddressed": "Plan internal review to finish well before the deadline."},
                  {"text": "The supplier must document experience with comparable assignments.", "type": "Mandatory", "interpretation": "References from similar projects are required.", "howAddressed": "Include two or three anonymised case references."},
                  {"text": "The solution will be evaluated on quality of the proposed approach.", "type": "Criterion", "interpretation": "The methodology section carries evaluation weight.", "howAddressed": "Make the approach concrete, phased and tied to the client's goals."}
                ]
                """;
            yield return new AiStreamEvent.Delta(json);
            yield return new AiStreamEvent.Completed(500, 150, "fake-model");
            yield break;
        }

        var text = $"""
            *(Generated by the offline stand-in AI — configure AzureOpenAI:Endpoint to use the real models.)*

            ## What the client is asking for
            A placeholder response for a **{request.Tier}** model request. The real model would analyse
            the working context ({request.SystemPrompt.Length + request.Messages.Sum(m => m.Content.Length)} characters of prompt) and respond here.

            ## Central themes
            - Theme one from the client material
            - Theme two from the client material

            ## Ambiguities and risks
            - This is canned text, not a real analysis.
            """;

        foreach (var chunk in text.Split(' '))
        {
            ct.ThrowIfCancellationRequested();
            yield return new AiStreamEvent.Delta(chunk + " ");
            await Task.Delay(10, ct);
        }
        yield return new AiStreamEvent.Completed(1000, 200, "fake-model");
    }

    private static string Json(string? value)
        => value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);
}
