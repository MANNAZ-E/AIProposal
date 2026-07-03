using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>Builds the AI service graph with fakes and empty configuration.</summary>
public static class TestServices
{
    public static WorkingContextService WorkingContext(LocalDbFixture db,
        IAiService? ai = null, IConfiguration? config = null)
        => new(db, new CondensationService(db, ai ?? new FakeAiService()),
            config ?? new ConfigurationBuilder().Build());

    public static GenerationService Generation(LocalDbFixture db, IAiService? ai = null)
        => new(db, ai ?? new FakeAiService(), new NullWebResearchService(),
            WorkingContext(db, ai), new ConfigurationBuilder().Build());

    public static ContentGenerationService ContentGeneration(LocalDbFixture db, IAiService? ai = null)
        => new(db, ai ?? new FakeAiService(), WorkingContext(db, ai));
}
