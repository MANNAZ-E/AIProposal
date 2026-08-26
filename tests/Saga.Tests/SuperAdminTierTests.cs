using Saga.Infrastructure.Services;

namespace Saga.Tests;

/// <summary>
/// The invariant that keeps the two flags sane: super admin is a tier on top of admin, never
/// beside it, and the last one of either cannot be removed.
/// </summary>
public class SuperAdminTierTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly AdminService _admin = new(db);
    private readonly UserService _users = new(db);

    private async Task<Guid> ElvAsync() => (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;

    private async Task<Guid> NewUserAsync(Guid actingAdmin, string label) =>
        await _admin.AddUserAsync(actingAdmin, $"{label}-{Guid.NewGuid():N}@mannaz.com", "Test Person");

    private async Task<AdminUserRow> RowAsync(Guid id) =>
        (await _admin.ListUsersAsync(await ElvAsync())).Single(u => u.Id == id);

    [Fact]
    public async Task Granting_super_admin_also_grants_admin()
    {
        var elv = await ElvAsync();
        var id = await NewUserAsync(elv, "promoted");

        await _admin.SetSuperAdminAsync(elv, id, true);

        var row = await RowAsync(id);
        Assert.True(row.IsSuperAdmin);
        Assert.True(row.IsAdmin);
    }

    [Fact]
    public async Task Revoking_admin_takes_the_super_admin_tier_with_it()
    {
        var elv = await ElvAsync();
        var id = await NewUserAsync(elv, "demoted");
        await _admin.SetSuperAdminAsync(elv, id, true);

        await _admin.SetAdminAsync(elv, id, false);

        var row = await RowAsync(id);
        Assert.False(row.IsAdmin);
        Assert.False(row.IsSuperAdmin);
    }

    [Fact]
    public async Task Removing_a_super_admin_clears_both_flags()
    {
        var elv = await ElvAsync();
        var id = await NewUserAsync(elv, "removed");
        await _admin.SetSuperAdminAsync(elv, id, true);

        await _admin.DeleteUserAsync(elv, id);

        var row = await RowAsync(id);
        Assert.True(row.IsDeleted);
        Assert.False(row.IsAdmin);
        Assert.False(row.IsSuperAdmin);
    }

    [Fact]
    public async Task A_plain_admin_cannot_promote_super_admins_or_edit_the_voice()
    {
        var elv = await ElvAsync();
        var plainAdmin = await NewUserAsync(elv, "plain-admin");
        await _admin.SetAdminAsync(elv, plainAdmin, true);
        var target = await NewUserAsync(elv, "target");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _admin.SetSuperAdminAsync(plainAdmin, target, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _admin.SaveVoiceAsync(plainAdmin, "tone", "about", "terms"));

        // The day-to-day powers are still theirs.
        await _admin.UpdateUserAsync(plainAdmin, target, "Renamed By Admin");
        Assert.Equal("Renamed By Admin", (await RowAsync(target)).DisplayName);
    }

    [Fact]
    public async Task A_super_admin_can_edit_the_voice()
    {
        var elv = await ElvAsync();

        await _admin.SaveVoiceAsync(elv, "  Direct  ", "  About Mannaz  ", "  Terms  ");

        var voice = await _admin.GetVoiceAsync();
        Assert.Equal("Direct", voice.ToneOfVoice);
        Assert.Equal("About Mannaz", voice.AboutMannaz);
        Assert.Equal("Terms", voice.Terminology);
    }

    [Fact]
    public async Task The_last_super_admin_cannot_be_demoted_or_removed()
    {
        var elv = await ElvAsync();

        // elv is the only seeded super admin, and cannot strip their own tier either way.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _admin.SetSuperAdminAsync(elv, elv, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _admin.SetAdminAsync(elv, elv, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _admin.DeleteUserAsync(elv, elv));

        // With a second one in place, the first can step down.
        var second = await NewUserAsync(elv, "second-super");
        await _admin.SetSuperAdminAsync(elv, second, true);
        await _admin.SetSuperAdminAsync(second, elv, false);
        Assert.False((await _admin.ListUsersAsync(second)).Single(u => u.Id == elv).IsSuperAdmin);

        // Put the seeded state back: this fixture's database is shared across the class.
        await _admin.SetSuperAdminAsync(second, elv, true);
        await _admin.SetSuperAdminAsync(elv, second, false);
    }

    [Fact]
    public async Task A_removed_user_cannot_be_promoted()
    {
        var elv = await ElvAsync();
        var id = await NewUserAsync(elv, "gone");
        await _admin.DeleteUserAsync(elv, id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _admin.SetSuperAdminAsync(elv, id, true));
    }
}
