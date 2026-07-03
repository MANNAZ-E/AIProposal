using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ProposalServiceTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private async Task<(Guid ElvId, Guid SdaId)> GetSeededUsersAsync()
    {
        var elv = await _users.FindByEmailAsync("elv@mannaz.com");
        var sda = await _users.FindByEmailAsync("sda@mannaz.com");
        return (elv!.Id, sda!.Id);
    }

    [Fact]
    public async Task Create_makes_creator_owner_and_shows_on_dashboard()
    {
        var (elv, _) = await GetSeededUsersAsync();

        var id = await _proposals.CreateAsync(elv, "Digital transformation", "Acme A/S", null, OutputFormat.PowerPoint);

        var dashboard = await _proposals.GetDashboardAsync(elv);
        var item = Assert.Single(dashboard, p => p.Id == id);
        Assert.Equal(ProposalRole.Owner, item.MyRole);
        Assert.True(item.IsOwnedByMe);
        Assert.False(item.IsArchived);
        Assert.Equal("Acme A/S", item.ClientName);
    }

    [Fact]
    public async Task Dashboard_is_sorted_by_created_date_descending()
    {
        var (elv, _) = await GetSeededUsersAsync();

        var first = await _proposals.CreateAsync(elv, "Older", "Client", null, OutputFormat.Word);
        var second = await _proposals.CreateAsync(elv, "Newer", "Client", null, OutputFormat.Word);

        var dashboard = await _proposals.GetDashboardAsync(elv);
        var ids = dashboard.Select(p => p.Id).ToList();
        Assert.True(ids.IndexOf(second) < ids.IndexOf(first));
    }

    [Fact]
    public async Task Share_gives_user_access_as_shared_with_you()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "Leadership program", "Beta ApS", null, OutputFormat.PowerPoint);

        await _proposals.ShareAsync(id, elv, "SDA@mannaz.com", ProposalRole.Editor);

        var dashboard = await _proposals.GetDashboardAsync(sda);
        var item = Assert.Single(dashboard, p => p.Id == id);
        Assert.Equal(ProposalRole.Editor, item.MyRole);
        Assert.False(item.IsOwnedByMe);
    }

    [Fact]
    public async Task Share_with_unknown_email_fails()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proposals.ShareAsync(id, elv, "nobody@mannaz.com", ProposalRole.Reader));
    }

    [Fact]
    public async Task Non_owner_cannot_share_archive_or_delete()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Editor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.ShareAsync(id, sda, "elv@mannaz.com", ProposalRole.Reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.SetArchivedAsync(id, sda, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.DeleteAsync(id, sda));
    }

    [Fact]
    public async Task Sharing_cannot_grant_or_demote_ownership()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Owner));

        // Re-sharing to the owner themselves must not demote them.
        await _proposals.ShareAsync(id, elv, "elv@mannaz.com", ProposalRole.Reader);
        var result = await _proposals.GetForUserAsync(id, elv);
        Assert.Equal(ProposalRole.Owner, result!.Value.Role);
    }

    [Fact]
    public async Task Archive_and_unarchive_round_trip()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);

        await _proposals.SetArchivedAsync(id, elv, archived: true);
        var dashboard = await _proposals.GetDashboardAsync(elv);
        Assert.True(Assert.Single(dashboard, p => p.Id == id).IsArchived);

        await _proposals.SetArchivedAsync(id, elv, archived: false);
        dashboard = await _proposals.GetDashboardAsync(elv);
        Assert.False(Assert.Single(dashboard, p => p.Id == id).IsArchived);
    }

    [Fact]
    public async Task Delete_removes_proposal_and_memberships()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Reader);

        await _proposals.DeleteAsync(id, elv);

        Assert.Null(await _proposals.GetForUserAsync(id, elv));
        Assert.DoesNotContain(await _proposals.GetDashboardAsync(sda), p => p.Id == id);
    }

    [Fact]
    public async Task Removed_member_loses_access()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Reader);

        await _proposals.RemoveMemberAsync(id, elv, sda);

        Assert.Null(await _proposals.GetForUserAsync(id, sda));
    }
}
