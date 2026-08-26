using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Extraction;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>Builds the AI service graph with fakes, wired the way Program.cs wires it.</summary>
public static class TestServices
{
    /// <summary>Dev-style prices so tests see non-zero costs where the model is priced.</summary>
    public static IConfiguration Pricing(params (string Key, string Value)[] extra)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pricing:UsdToDkk"] = "7",
            ["Pricing:Models:fake-model:InputPer1M"] = "1.25",
            ["Pricing:Models:fake-model:OutputPer1M"] = "10",
            // Keyed by meter, not analyzer — the real West Europe rates, so a test cost is the
            // cost the app would actually book.
            ["Pricing:ContentUnderstanding:DocumentPagesMinimalPer1000"] = "0.01",
            ["Pricing:ContentUnderstanding:DocumentPagesBasicPer1000"] = "1.00",
            ["Pricing:ContentUnderstanding:DocumentPagesStandardPer1000"] = "5.00",
            ["Pricing:ContentUnderstanding:ContextualizationTokensPer1000"] = "0.001",
        }.Concat(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
         .ToDictionary(e => e.Key, e => e.Value)).Build();

    /// <summary>
    /// Wraps an AI service in the usage decorator, exactly as Program.cs does — so tests
    /// exercise the same metering path the app runs.
    /// </summary>
    /// <param name="notifier">
    /// Pass one to watch the live-update event the app bar's spend figure runs on; otherwise the
    /// decorator gets a throwaway, since publishing to nobody is what the tests care about least.
    /// </param>
    public static IAiService Ai(LocalDbFixture db, IAiService? inner = null,
        IConfiguration? config = null, AiUsageNotifier? notifier = null)
        => new UsageTrackingAiService(inner ?? new FakeAiService(), db,
            new PricingService(config ?? Pricing()), notifier ?? new AiUsageNotifier());

    /// <summary>Wraps an extractor in the usage decorator, as Program.cs does for the billed one.</summary>
    public static IDocumentTextExtractor Extractor(LocalDbFixture db, IDocumentTextExtractor inner,
        IConfiguration? config = null, AiUsageNotifier? notifier = null)
        => new UsageTrackingTextExtractor(inner, ContentUnderstandingExtractor.AnalyzerId, db,
            new PricingService(config ?? Pricing()), notifier ?? new AiUsageNotifier());

    public static WorkingContextService WorkingContext(LocalDbFixture db,
        IAiService? ai = null, IConfiguration? config = null)
        => new(db, new CondensationService(db, ai ?? Ai(db)),
            config ?? new ConfigurationBuilder().Build());

    public static GenerationService Generation(LocalDbFixture db, IAiService? ai = null)
    {
        ai ??= Ai(db);
        return new(db, ai, new NullWebResearchService(), WorkingContext(db, ai),
            new AiUsageService(db, new PricingService(Pricing())));
    }

    public static ContentGenerationService ContentGeneration(LocalDbFixture db, IAiService? ai = null)
    {
        ai ??= Ai(db);
        return new(db, ai, WorkingContext(db, ai));
    }

    /// <summary>
    /// The proposal's first document type — the one the upload form pre-selects. Tests that add
    /// material straight to the database need it, since every document is filed under a type.
    /// </summary>
    public static async Task<Guid> DefaultDocumentTypeAsync(LocalDbFixture db, Guid proposalId)
    {
        await using var check = db.CreateDbContext();
        return (await check.DocumentTypes.Where(t => t.ProposalId == proposalId)
            .OrderBy(t => t.SortOrder).FirstAsync()).Id;
    }

    public static AiUsageService Usage(LocalDbFixture db, IConfiguration? config = null)
        => new(db, new PricingService(config ?? Pricing()));
}
