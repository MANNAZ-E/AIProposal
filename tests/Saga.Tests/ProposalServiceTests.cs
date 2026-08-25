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
    public async Task Delete_hides_the_proposal_but_keeps_it_in_the_recycle_bin()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Reader);

        await _proposals.DeleteAsync(id, elv);

        Assert.Null(await _proposals.GetForUserAsync(id, elv));
        Assert.DoesNotContain(await _proposals.GetDashboardAsync(sda), p => p.Id == id);

        // Both members see it in the recycle bin, with the deletion timestamp.
        var bin = await _proposals.GetRecycleBinAsync(sda);
        var item = Assert.Single(bin, p => p.Id == id);
        Assert.NotNull(item.DeletedAt);
    }

    [Fact]
    public async Task Any_team_member_can_restore_from_the_recycle_bin()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Reader);
        await _proposals.DeleteAsync(id, elv);

        await _proposals.RestoreAsync(id, sda);

        Assert.NotNull(await _proposals.GetForUserAsync(id, elv));
        Assert.Contains(await _proposals.GetDashboardAsync(elv), p => p.Id == id);
        Assert.DoesNotContain(await _proposals.GetRecycleBinAsync(elv), p => p.Id == id);
    }

    [Fact]
    public async Task Delete_requires_owner()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Editor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _proposals.DeleteAsync(id, sda));
    }

    [Fact]
    public async Task Settings_rename_updates_title_and_client()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "Old name", "Old client", null, OutputFormat.PowerPoint);

        await _proposals.UpdateDetailsAsync(id, elv, "  New name  ", "  New client  ");

        var (proposal, _) = (await _proposals.GetForUserAsync(id, elv))!.Value;
        Assert.Equal("New name", proposal.Title);
        Assert.Equal("New client", proposal.ClientName);
    }

    [Fact]
    public async Task Settings_rename_requires_owner()
    {
        var (elv, sda) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(id, elv, "sda@mannaz.com", ProposalRole.Editor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.UpdateDetailsAsync(id, sda, "Hijacked", "Client"));
    }

    /// <summary>
    /// A proposal is always named, but the client is not: work often starts before anyone knows
    /// whose it is, so blanking the client name clears it rather than being refused.
    /// </summary>
    [Fact]
    public async Task Settings_rename_requires_a_title_but_not_a_client()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proposals.UpdateDetailsAsync(id, elv, "   ", "C"));

        await _proposals.UpdateDetailsAsync(id, elv, "P", "   ");

        var (proposal, _) = (await _proposals.GetForUserAsync(id, elv))!.Value;
        Assert.Equal("P", proposal.Title);
        Assert.Equal("", proposal.ClientName);
    }

    [Fact]
    public async Task Client_research_settings_round_trip_and_blank_clears()
    {
        var (elv, _) = await GetSeededUsersAsync();
        var id = await _proposals.CreateAsync(elv, "P", "Acme A/S", null, OutputFormat.PowerPoint);

        await _proposals.SetClientResearchAsync(id, elv, "  Acme Group A/S  ", "  https://acme.example  ");
        var (proposal, _) = (await _proposals.GetForUserAsync(id, elv))!.Value;
        Assert.Equal("Acme Group A/S", proposal.ResearchClientName);
        Assert.Equal("https://acme.example", proposal.ClientWebsite);

        await _proposals.SetClientResearchAsync(id, elv, "  ", null);
        (proposal, _) = (await _proposals.GetForUserAsync(id, elv))!.Value;
        Assert.Null(proposal.ResearchClientName);
        Assert.Null(proposal.ClientWebsite);
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
