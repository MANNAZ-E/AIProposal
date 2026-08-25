using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class UserAdminTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly AdminService _admin = new(db);
    private readonly UserService _users = new(db);

    private async Task<Guid> ElvIdAsync() => (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
    private async Task<Guid> SdaIdAsync() => (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;

    [Fact]
    public async Task Add_user_creates_an_active_row()
    {
        var elv = await ElvIdAsync();
        var email = $"newbie-{Guid.NewGuid():N}@mannaz.com";

        var id = await _admin.AddUserAsync(elv, email, "New Person");

        var row = Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id);
        Assert.Equal(email, row.Email);
        Assert.Equal("New Person", row.DisplayName);
        Assert.False(row.IsAdmin);
        Assert.False(row.IsDeleted);
    }

    [Fact]
    public async Task Add_user_is_adopted_by_sign_in_matching_by_email()
    {
        var elv = await ElvIdAsync();
        var email = $"colleague-{Guid.NewGuid():N}@mannaz.com";
        var addedId = await _admin.AddUserAsync(elv, email, "Colleague");

        // Mirrors what happens on that person's first real sign-in: matched by email, no duplicate row.
        var signedIn = await _users.GetOrCreateAsync(email, "Colleague (from Entra)", "entra-object-id");

        Assert.Equal(addedId, signedIn.Id);
    }

    [Fact]
    public async Task Add_user_with_existing_active_email_throws()
    {
        var elv = await ElvIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _admin.AddUserAsync(elv, "elv@mannaz.com", "Someone else"));
    }

    [Fact]
    public async Task Add_user_with_invalid_email_throws()
    {
        var elv = await ElvIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _admin.AddUserAsync(elv, "not-an-email", "Name"));
    }

    [Fact]
    public async Task Add_user_by_non_admin_is_rejected()
    {
        var sda = await SdaIdAsync(); // seeded, not an admin

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _admin.AddUserAsync(sda, "x@mannaz.com", "X"));
    }

    [Fact]
    public async Task Removing_a_user_blocks_recreates_the_same_row_and_recreate_restores_it()
    {
        var elv = await ElvIdAsync();
        var email = $"leaver-{Guid.NewGuid():N}@mannaz.com";
        var id = await _admin.AddUserAsync(elv, email, "Leaver");

        await _admin.DeleteUserAsync(elv, id);
        var deletedRow = Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id);
        Assert.True(deletedRow.IsDeleted);
        Assert.NotNull(deletedRow.DeletedAt);

        // Re-adding the same email recreates the same row instead of erroring or duplicating.
        var recreatedId = await _admin.AddUserAsync(elv, email, "Leaver Returned");

        Assert.Equal(id, recreatedId);
        var restoredRow = Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id);
        Assert.False(restoredRow.IsDeleted);
        Assert.Equal("Leaver Returned", restoredRow.DisplayName);
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var elv = await ElvIdAsync();
        var id = await _admin.AddUserAsync(elv, $"once-{Guid.NewGuid():N}@mannaz.com", "Once");

        await _admin.DeleteUserAsync(elv, id);
        await _admin.DeleteUserAsync(elv, id); // must not throw

        Assert.True(Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).IsDeleted);
    }

    [Fact]
    public async Task Restore_reactivates_a_removed_user_without_restoring_admin_rights()
    {
        var elv = await ElvIdAsync();
        var id = await _admin.AddUserAsync(elv, $"comeback-{Guid.NewGuid():N}@mannaz.com", "Comeback");
        await _admin.SetAdminAsync(elv, id, true);
        await _admin.DeleteUserAsync(elv, id);

        await _admin.RestoreUserAsync(elv, id);

        var row = Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id);
        Assert.False(row.IsDeleted);
        Assert.False(row.IsAdmin); // cleared on delete, must be re-granted explicitly
    }

    [Fact]
    public async Task Update_user_changes_display_name_only()
    {
        var elv = await ElvIdAsync();
        var id = await _admin.AddUserAsync(elv, $"rename-{Guid.NewGuid():N}@mannaz.com", "Old Name");

        await _admin.UpdateUserAsync(elv, id, "New Name");

        Assert.Equal("New Name", Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).DisplayName);
    }

    [Fact]
    public async Task Set_admin_promotes_and_revokes()
    {
        var elv = await ElvIdAsync();
        var id = await _admin.AddUserAsync(elv, $"promo-{Guid.NewGuid():N}@mannaz.com", "Promotable");

        await _admin.SetAdminAsync(elv, id, true);
        Assert.True(Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).IsAdmin);

        await _admin.SetAdminAsync(elv, id, false);
        Assert.False(Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).IsAdmin);
    }

    [Fact]
    public async Task Admin_cannot_revoke_their_own_admin_access()
    {
        var elv = await ElvIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _admin.SetAdminAsync(elv, elv, false));
    }

    [Fact]
    public async Task Admin_cannot_remove_their_own_account()
    {
        var elv = await ElvIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _admin.DeleteUserAsync(elv, elv));
    }

    [Fact]
    public async Task Demoting_or_removing_an_admin_who_is_not_the_last_one_succeeds()
    {
        var elv = await ElvIdAsync();
        var id = await _admin.AddUserAsync(elv, $"second-admin-{Guid.NewGuid():N}@mannaz.com", "Second Admin");
        await _admin.SetAdminAsync(elv, id, true);

        // elv remains admin throughout, so demoting/removing the second admin must be allowed.
        await _admin.SetAdminAsync(elv, id, false);
        Assert.False(Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).IsAdmin);

        await _admin.SetAdminAsync(elv, id, true);
        await _admin.DeleteUserAsync(elv, id);
        Assert.True(Assert.Single(await _admin.ListUsersAsync(), u => u.Id == id).IsDeleted);
    }
}
