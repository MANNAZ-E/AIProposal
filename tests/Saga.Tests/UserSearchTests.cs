using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>What the Bid Team picker offers as you type.</summary>
public class UserSearchTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly UserService _users = new(db);
    private readonly AdminService _admin = new(db);

    private async Task<Guid> ElvAsync() => (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;

    [Fact]
    public async Task Matches_a_fragment_of_the_name()
    {
        var hits = await _users.SearchActiveAsync("Stefanie");
        Assert.Contains(hits, h => h.Email == "sda@mannaz.com");
    }

    [Fact]
    public async Task Matches_a_fragment_of_the_email()
    {
        var hits = await _users.SearchActiveAsync("sda@");
        Assert.Contains(hits, h => h.Email == "sda@mannaz.com");
    }

    [Fact]
    public async Task Matching_is_case_insensitive()
    {
        // The seeded collation is case-insensitive, which is what the picker relies on: a
        // consultant typing "stefanie" should not have to guess the capitalisation.
        var hits = await _users.SearchActiveAsync("stefanie");
        Assert.Contains(hits, h => h.Email == "sda@mannaz.com");
    }

    [Fact]
    public async Task A_blank_term_returns_a_starting_list()
    {
        var hits = await _users.SearchActiveAsync("   ");
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task Removed_users_are_never_offered()
    {
        var elv = await ElvAsync();
        var email = $"searchable-{Guid.NewGuid():N}@mannaz.com";
        var id = await _admin.AddUserAsync(elv, email, "Findable Person");

        Assert.Contains(await _users.SearchActiveAsync(email), h => h.Id == id);

        await _admin.DeleteUserAsync(elv, id);
        Assert.DoesNotContain(await _users.SearchActiveAsync(email), h => h.Id == id);
    }

    [Fact]
    public async Task The_limit_is_honoured()
    {
        var hits = await _users.SearchActiveAsync("mannaz.com", limit: 2);
        Assert.Equal(2, hits.Count);
    }
}
