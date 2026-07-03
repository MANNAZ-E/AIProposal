using Microsoft.EntityFrameworkCore;
using Saga.Infrastructure.Data;

namespace Saga.Tests;

/// <summary>
/// Creates a throwaway LocalDB database per test class so tests run against real SQL Server
/// behavior (rowversion, cascade rules) instead of a provider that fakes it.
/// </summary>
public sealed class LocalDbFixture : IDbContextFactory<SagaDbContext>, IAsyncLifetime
{
    private readonly string _dbName = $"SagaTest_{Guid.NewGuid():N}";

    private DbContextOptions<SagaDbContext> Options => new DbContextOptionsBuilder<SagaDbContext>()
        .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={_dbName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True")
        .Options;

    public SagaDbContext CreateDbContext() => new(Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }
}
