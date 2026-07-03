namespace Saga.Core.Abstractions;

/// <summary>
/// Live web research for the client profile (Grounding with Bing Search via Azure AI Foundry).
/// Returns markdown findings with source citations, or null when research is unavailable —
/// the profile prompt then falls back to model knowledge with a visible caveat.
/// </summary>
public interface IWebResearchService
{
    Task<string?> ResearchClientAsync(string clientName, string? assignmentContext,
        CancellationToken ct = default);
}

/// <summary>Used until the Foundry project + Bing connection are configured.</summary>
public class NullWebResearchService : IWebResearchService
{
    public Task<string?> ResearchClientAsync(string clientName, string? assignmentContext,
        CancellationToken ct = default) => Task.FromResult<string?>(null);
}
