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
            ["Pricing:ContentUnderstanding:prebuilt-layout:Per1000Pages"] = "10",
        }.Concat(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
         .ToDictionary(e => e.Key, e => e.Value)).Build();

    /// <summary>
    /// Wraps an AI service in the usage decorator, exactly as Program.cs does — so tests
    /// exercise the same metering path the app runs.
    /// </summary>
    public static IAiService Ai(LocalDbFixture db, IAiService? inner = null, IConfiguration? config = null)
        => new UsageTrackingAiService(inner ?? new FakeAiService(), db,
            new PricingService(config ?? Pricing()));

    /// <summary>Wraps an extractor in the usage decorator, as Program.cs does for the billed one.</summary>
    public static IDocumentTextExtractor Extractor(LocalDbFixture db, IDocumentTextExtractor inner,
        IConfiguration? config = null)
        => new UsageTrackingTextExtractor(inner, ContentUnderstandingExtractor.AnalyzerId, db,
            new PricingService(config ?? Pricing()));

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

    public static AiUsageService Usage(LocalDbFixture db, IConfiguration? config = null)
        => new(db, new PricingService(config ?? Pricing()));
}
