using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ImpersonationAuditServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ImpersonationAuditService _audit = new(db);
    private readonly UserService _users = new(db);

    private async Task<(Guid ElvId, Guid SdaId)> GetSeededUsersAsync()
    {
        var elv = await _users.FindByEmailAsync("elv@mannaz.com");
        var sda = await _users.FindByEmailAsync("sda@mannaz.com");
        return (elv!.Id, sda!.Id);
    }

    [Fact]
    public async Task Start_writes_an_open_session()
    {
        var (elv, sda) = await GetSeededUsersAsync();

        var id = await _audit.StartAsync(elv, sda);

        var row = Assert.Single(await _audit.GetAllAsync(), r => r.Id == id);
        Assert.Equal("Emil Lindeløv Vestergaard", row.AdminName);
        Assert.Equal("Stefanie Baptiste", row.TargetName);
        Assert.Null(row.EndedAt);
        Assert.Null(row.EndReason);
    }

    [Fact]
    public async Task End_closes_the_session_with_its_reason()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _audit.StartAsync(elv, sda);

        await _audit.EndAsync(id, ImpersonationEndReason.StoppedByAdmin);

        var row = Assert.Single(await _audit.GetAllAsync(), r => r.Id == id);
        Assert.NotNull(row.EndedAt);
        Assert.Equal(ImpersonationEndReason.StoppedByAdmin, row.EndReason);
    }

    [Fact]
    public async Task End_is_idempotent_and_keeps_the_first_reason()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _audit.StartAsync(elv, sda);

        await _audit.EndAsync(id, ImpersonationEndReason.StoppedByAdmin);
        await _audit.EndAsync(id, ImpersonationEndReason.CircuitDisconnected); // must not overwrite

        var row = Assert.Single(await _audit.GetAllAsync(), r => r.Id == id);
        Assert.Equal(ImpersonationEndReason.StoppedByAdmin, row.EndReason);
    }

    [Fact]
    public async Task End_on_an_unknown_session_does_not_throw()
    {
        await _audit.EndAsync(Guid.NewGuid(), ImpersonationEndReason.CircuitDisconnected);
    }

    [Fact]
    public async Task CloseAbandonedSessions_closes_only_open_rows_as_disconnected()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var stillOpen = await _audit.StartAsync(elv, sda);
        var alreadyClosed = await _audit.StartAsync(elv, sda);
        await _audit.EndAsync(alreadyClosed, ImpersonationEndReason.StoppedByAdmin);

        await _audit.CloseAbandonedSessionsAsync();

        var rows = await _audit.GetAllAsync();
        var openRow = Assert.Single(rows, r => r.Id == stillOpen);
        Assert.NotNull(openRow.EndedAt);
        Assert.Equal(ImpersonationEndReason.CircuitDisconnected, openRow.EndReason);

        // Untouched: it already had a real end reason before the cleanup ran.
        var closedRow = Assert.Single(rows, r => r.Id == alreadyClosed);
        Assert.Equal(ImpersonationEndReason.StoppedByAdmin, closedRow.EndReason);
    }

    [Fact]
    public async Task GetAll_orders_newest_first()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var first = await _audit.StartAsync(elv, sda);
        var second = await _audit.StartAsync(elv, sda);

        var rows = await _audit.GetAllAsync();
        var firstIndex = rows.FindIndex(r => r.Id == first);
        var secondIndex = rows.FindIndex(r => r.Id == second);

        Assert.True(secondIndex < firstIndex);
    }
}
