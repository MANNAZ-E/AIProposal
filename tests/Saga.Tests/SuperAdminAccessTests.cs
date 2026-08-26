using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>
/// The super-admin tier: oversight is read-only, and the one door it opens wider is the team list,
/// so a super admin who needs to work on a bid joins it rather than editing from outside.
/// </summary>
public class SuperAdminAccessTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly AdminService _admin = new(db);
    private readonly TeamChatService _teamChat = new(db, new TeamChatNotifier());

    // elv is seeded as the super admin; sda owns the bids under test so elv is a genuine outsider.
    private async Task<Guid> ElvAsync() => (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
    private async Task<Guid> SdaAsync() => (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
    private async Task<Guid> MknAsync() => (await _users.FindByEmailAsync("mkn@mannaz.com"))!.Id;

    [Fact]
    public async Task Seeded_super_admin_is_also_an_admin()
    {
        var elv = await _users.FindByEmailAsync("elv@mannaz.com");
        Assert.True(elv!.IsSuperAdmin);
        Assert.True(elv.IsAdmin);
    }

    [Fact]
    public async Task Super_admin_reads_a_bid_they_are_not_on_as_a_reader()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "Someone else's bid", "Acme A/S", null, OutputFormat.PowerPoint);

        var result = await _proposals.GetForUserAsync(id, elv);

        Assert.NotNull(result);
        Assert.Equal(ProposalRole.Reader, result!.Value.Role);
    }

    [Fact]
    public async Task Reading_a_bid_does_not_put_the_super_admin_on_its_team()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        await _proposals.GetForUserAsync(id, elv);

        var (proposal, _) = (await _proposals.GetForUserAsync(id, sda))!.Value;
        Assert.Single(proposal.Members);
        Assert.DoesNotContain(proposal.Members, m => m.UserId == elv);
        // Nor does it turn up on their own dashboard as if it were theirs.
        Assert.DoesNotContain(await _proposals.GetDashboardAsync(elv), p => p.Id == id);
    }

    [Fact]
    public async Task Super_admin_cannot_rename_archive_or_delete_a_bid_they_are_not_on()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.UpdateDetailsAsync(id, elv, "Renamed", "C"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.SetArchivedAsync(id, elv, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.DeleteAsync(id, elv));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.SetOutputFormatAsync(id, elv, OutputFormat.Word));
    }

    /// <summary>
    /// The whole point of the read-only tier: joining is a deliberate, visible act that the owner
    /// can see on the team list, and only then does editing unlock.
    /// </summary>
    [Fact]
    public async Task Super_admin_gains_edit_rights_only_by_adding_themselves()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.SetOutputFormatAsync(id, elv, OutputFormat.Word));

        await _proposals.AddMemberAsync(id, elv, elv, ProposalRole.Editor);

        await _proposals.SetOutputFormatAsync(id, elv, OutputFormat.Word);
        var (proposal, role) = (await _proposals.GetForUserAsync(id, elv))!.Value;
        Assert.Equal(ProposalRole.Editor, role);
        Assert.Equal(OutputFormat.Word, proposal.OutputFormat);
        Assert.Contains(proposal.Members, m => m.UserId == elv);
    }

    [Fact]
    public async Task Super_admin_can_read_the_team_chat_but_not_post_in_it()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);
        var threadId = await _teamChat.StartThreadAsync(id, sda, "Anyone seen the tender?");

        // Reading is oversight.
        Assert.Single(await _teamChat.ListThreadsAsync(id, elv));
        Assert.NotEmpty(await _teamChat.ListAsync(threadId, elv));

        // Joining the conversation is not.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _teamChat.PostAsync(threadId, elv, "Looks fine to me"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _teamChat.StartThreadAsync(id, elv, "A thread of my own"));

        // With a seat on the team, both work.
        await _proposals.AddMemberAsync(id, elv, elv, ProposalRole.Reader);
        await _teamChat.PostAsync(threadId, elv, "Looks fine to me");
    }

    [Fact]
    public async Task A_plain_non_member_still_sees_nothing()
    {
        var (sda, mkn) = (await SdaAsync(), await MknAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        Assert.Null(await _proposals.GetForUserAsync(id, mkn));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _proposals.GetAllAsync(mkn));
    }

    [Fact]
    public async Task GetAll_lists_every_live_bid_with_the_viewer_s_own_role()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var theirs = await _proposals.CreateAsync(sda, "Theirs", "C", null, OutputFormat.PowerPoint);
        var mine = await _proposals.CreateAsync(elv, "Mine", "C", null, OutputFormat.PowerPoint);
        var gone = await _proposals.CreateAsync(sda, "Deleted", "C", null, OutputFormat.PowerPoint);
        await _proposals.DeleteAsync(gone, sda);

        var all = await _proposals.GetAllAsync(elv);

        Assert.Null(Assert.Single(all, p => p.Id == theirs).MyRole);
        Assert.Equal(ProposalRole.Owner, Assert.Single(all, p => p.Id == mine).MyRole);
        Assert.DoesNotContain(all, p => p.Id == gone);
    }

    // ---- Team management: the one door that is wider than Owner.

    [Fact]
    public async Task An_admin_on_the_bid_can_add_and_remove_members()
    {
        var (elv, sda, mkn) = (await ElvAsync(), await SdaAsync(), await MknAsync());
        var adminId = await _admin.AddUserAsync(elv, $"bid-admin-{Guid.NewGuid():N}@mannaz.com", "Bid Admin");
        await _admin.SetAdminAsync(elv, adminId, true);

        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.AddMemberAsync(id, sda, adminId, ProposalRole.Editor);

        await _proposals.AddMemberAsync(id, adminId, mkn, ProposalRole.Reader);
        Assert.Equal(ProposalRole.Reader, (await _proposals.GetForUserAsync(id, mkn))!.Value.Role);

        await _proposals.RemoveMemberAsync(id, adminId, mkn);
        Assert.Null(await _proposals.GetForUserAsync(id, mkn));
    }

    [Fact]
    public async Task An_admin_who_is_not_on_the_bid_cannot_touch_its_team()
    {
        var (elv, sda, mkn) = (await ElvAsync(), await SdaAsync(), await MknAsync());
        var adminId = await _admin.AddUserAsync(elv, $"outside-admin-{Guid.NewGuid():N}@mannaz.com", "Outside Admin");
        await _admin.SetAdminAsync(elv, adminId, true);
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.AddMemberAsync(id, adminId, mkn, ProposalRole.Reader));
    }

    [Fact]
    public async Task A_plain_editor_still_cannot_touch_the_team()
    {
        var (elv, sda, mkn) = (await ElvAsync(), await SdaAsync(), await MknAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.AddMemberAsync(id, sda, mkn, ProposalRole.Editor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.AddMemberAsync(id, mkn, elv, ProposalRole.Reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _proposals.RemoveMemberAsync(id, mkn, sda));
    }

    [Fact]
    public async Task Adding_a_removed_user_is_refused()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var goneId = await _admin.AddUserAsync(elv, $"gone-{Guid.NewGuid():N}@mannaz.com", "Gone");
        var email = (await _admin.ListUsersAsync(elv)).Single(u => u.Id == goneId).Email;
        await _admin.DeleteUserAsync(elv, goneId);

        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proposals.AddMemberAsync(id, sda, goneId, ProposalRole.Editor));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proposals.ShareAsync(id, sda, email, ProposalRole.Editor));
    }

    // ---- The recycle bin belongs to the owner.

    [Fact]
    public async Task Only_the_owner_sees_and_restores_their_deleted_bid()
    {
        var (sda, mkn) = (await SdaAsync(), await MknAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.AddMemberAsync(id, sda, mkn, ProposalRole.Editor);
        await _proposals.DeleteAsync(id, sda);

        Assert.Contains(await _proposals.GetRecycleBinAsync(sda), p => p.Id == id);
        Assert.DoesNotContain(await _proposals.GetRecycleBinAsync(mkn), p => p.Id == id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _proposals.RestoreAsync(id, mkn));

        await _proposals.RestoreAsync(id, sda);
        Assert.Contains(await _proposals.GetDashboardAsync(sda), p => p.Id == id);
    }

    [Fact]
    public async Task A_super_admin_restores_from_the_admin_recycle_bin_instead()
    {
        var (elv, sda) = (await ElvAsync(), await SdaAsync());
        var id = await _proposals.CreateAsync(sda, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.DeleteAsync(id, sda);

        // Not through their own bin — they do not own it — but the admin door is open.
        Assert.DoesNotContain(await _proposals.GetRecycleBinAsync(elv), p => p.Id == id);
        Assert.Contains(await _admin.GetDeletedProposalsAsync(elv), p => p.ProposalId == id);

        await _proposals.RestoreAsAdminAsync(id);
        Assert.Contains(await _proposals.GetDashboardAsync(sda), p => p.Id == id);
    }
}
