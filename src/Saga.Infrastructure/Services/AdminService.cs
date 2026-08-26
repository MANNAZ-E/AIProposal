using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>Spend for one proposal, as shown on the admin roll-up.</summary>
public record ProposalSpend(Guid ProposalId, string Title, string ClientName, int Calls,
    long InputTokens, long CachedInputTokens, long OutputTokens, long Pages, decimal CostUsd,
    DateTimeOffset? LastCallAt);

/// <summary>One row in the admin recycle bin.</summary>
public record DeletedProposal(Guid ProposalId, string Title, string ClientName, string OwnerName,
    DateTimeOffset? DeletedAt);

/// <summary>One row in the admin Users table.</summary>
public record AdminUserRow(Guid Id, string Email, string DisplayName, bool IsAdmin,
    bool IsSuperAdmin, bool IsDeleted, DateTimeOffset? DeletedAt, DateTimeOffset CreatedAt);

/// <summary>Mannaz voice settings + the usage/cost view (spec: usage logging with simple view).</summary>
public class AdminService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<MannazVoiceSettings> GetVoiceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MannazVoiceSettings.FirstAsync(ct);
    }

    /// <summary>
    /// Super admin only: the voice is prepended to every generated word, so editing it is a
    /// system-owner action rather than day-to-day administration. Reading it stays open because
    /// the generation pipeline loads it on every run.
    /// </summary>
    public async Task SaveVoiceAsync(Guid actingUserId, string toneOfVoice, string aboutMannaz,
        string terminology, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureSuperAdminAsync(db, actingUserId, ct);
        var voice = await db.MannazVoiceSettings.FirstAsync(ct);
        voice.ToneOfVoice = toneOfVoice.Trim();
        voice.AboutMannaz = aboutMannaz.Trim();
        voice.Terminology = terminology.Trim();
        voice.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Every soft-deleted proposal, across all users, newest deletion first.</summary>
    public async Task<List<DeletedProposal>> GetDeletedProposalsAsync(Guid actingAdminId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);
        return await db.Proposals
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new DeletedProposal(p.Id, p.Title, p.ClientName, p.Owner!.DisplayName, p.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>Spend per proposal, newest activity first. Includes archived proposals and chat.</summary>
    public async Task<List<ProposalSpend>> GetUsageAsync(Guid actingAdminId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);
        var rows = await db.AiUsage
            .Where(r => r.ProposalId != null)
            .GroupBy(r => new { r.ProposalId, r.Proposal!.Title, r.Proposal.ClientName })
            .Select(g => new
            {
                g.Key.ProposalId,
                g.Key.Title,
                g.Key.ClientName,
                Calls = g.Count(),
                InputTokens = g.Sum(r => (long)r.InputTokens),
                CachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                OutputTokens = g.Sum(r => (long)r.OutputTokens),
                Pages = g.Sum(r => (long)((r.MinimalPages ?? 0) + (r.BasicPages ?? 0)
                    + (r.StandardPages ?? 0))),
                CostUsd = g.Sum(r => r.EstimatedCostUsd),
                LastCallAt = g.Max(r => (DateTimeOffset?)r.StartedAt),
            })
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.LastCallAt)
            .Select(r => new ProposalSpend(r.ProposalId!.Value, r.Title, r.ClientName, r.Calls,
                r.InputTokens, r.CachedInputTokens, r.OutputTokens, r.Pages, r.CostUsd,
                r.LastCallAt))
            .ToList();
    }

    /// <summary>Every user, active and deleted, for the admin Users table.</summary>
    public async Task<List<AdminUserRow>> ListUsersAsync(Guid actingAdminId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);
        return await db.Users
            .OrderBy(u => u.IsDeleted).ThenBy(u => u.DisplayName)
            .Select(u => new AdminUserRow(u.Id, u.Email, u.DisplayName, u.IsAdmin, u.IsSuperAdmin,
                u.IsDeleted, u.DeletedAt, u.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Adds a user by email so they can be shared with or promoted before they've ever signed
    /// in. Adding an email that belongs to a removed user recreates it in place instead of
    /// erroring, so their proposals and history come back under the same id.
    /// </summary>
    public async Task<Guid> AddUserAsync(Guid actingAdminId, string email, string displayName,
        CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        try { _ = new MailAddress(email); }
        catch (FormatException) { throw new InvalidOperationException("Enter a valid email address."); }
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Enter a name.");
        displayName = displayName.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existing is not null)
        {
            if (!existing.IsDeleted)
                throw new InvalidOperationException($"A user with email '{email}' already exists.");

            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.DisplayName = displayName;
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            EntraObjectId = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user.Id;
    }

    public async Task UpdateUserAsync(Guid actingAdminId, Guid userId, string displayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Enter a name.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.DisplayName = displayName.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task SetAdminAsync(Guid actingAdminId, Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);

        if (userId == actingAdminId && !isAdmin)
            throw new InvalidOperationException("You cannot remove your own admin access.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (user.IsAdmin && !isAdmin)
        {
            await EnsureNotLastAdminAsync(db, userId, ct);
            if (user.IsSuperAdmin)
                await EnsureNotLastSuperAdminAsync(db, userId, ct);
        }

        user.IsAdmin = isAdmin;
        // Super admin is a tier on top of admin, never beside it: taking admin away takes the
        // tier with it, so there is never a super admin who cannot reach /admin.
        if (!isAdmin) user.IsSuperAdmin = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Grants or revokes the super-admin tier. Only a super admin can hand it out; granting also
    /// grants plain admin, keeping the tier a strict superset rather than a parallel flag.
    /// </summary>
    public async Task SetSuperAdminAsync(Guid actingAdminId, Guid userId, bool isSuperAdmin,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureSuperAdminAsync(db, actingAdminId, ct);

        if (userId == actingAdminId && !isSuperAdmin)
            throw new InvalidOperationException("You cannot remove your own super admin access.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (user.IsDeleted)
            throw new InvalidOperationException("A removed user cannot be promoted.");
        if (user.IsSuperAdmin && !isSuperAdmin)
            await EnsureNotLastSuperAdminAsync(db, userId, ct);

        user.IsSuperAdmin = isSuperAdmin;
        if (isSuperAdmin) user.IsAdmin = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Soft delete: the user can no longer sign in, but their proposals, memberships and usage
    /// history stay intact. Also clears admin rights, so a later restore never silently hands
    /// them back without a deliberate re-grant.
    /// </summary>
    public async Task DeleteUserAsync(Guid actingAdminId, Guid userId, CancellationToken ct = default)
    {
        if (userId == actingAdminId)
            throw new InvalidOperationException("You cannot remove your own account.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (user.IsDeleted) return;
        if (user.IsAdmin)
            await EnsureNotLastAdminAsync(db, userId, ct);
        if (user.IsSuperAdmin)
            await EnsureNotLastSuperAdminAsync(db, userId, ct);

        user.IsDeleted = true;
        user.DeletedAt = DateTimeOffset.UtcNow;
        user.IsAdmin = false;
        user.IsSuperAdmin = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Restores a removed user. Admin rights are not brought back automatically.</summary>
    public async Task RestoreUserAsync(Guid actingAdminId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureAdminAsync(db, actingAdminId, ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!user.IsDeleted) return;
        user.IsDeleted = false;
        user.DeletedAt = null;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<User> EnsureAdminAsync(SagaDbContext db, Guid actingAdminId, CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Id == actingAdminId, ct);
        if (admin is null || admin.IsDeleted || !admin.IsAdmin)
            throw new UnauthorizedAccessException("This action requires admin access.");
        return admin;
    }

    private static async Task<User> EnsureSuperAdminAsync(SagaDbContext db, Guid actingAdminId,
        CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Id == actingAdminId, ct);
        if (admin is null || admin.IsDeleted || !admin.IsSuperAdmin)
            throw new UnauthorizedAccessException("This action requires super admin access.");
        return admin;
    }

    /// <summary>Guards against zeroing out every admin account. Caller has already confirmed the target is an admin.</summary>
    private static async Task EnsureNotLastAdminAsync(SagaDbContext db, Guid userId, CancellationToken ct)
    {
        var otherAdmins = await db.Users.CountAsync(u => u.IsAdmin && !u.IsDeleted && u.Id != userId, ct);
        if (otherAdmins == 0)
            throw new InvalidOperationException("At least one admin must remain.");
    }

    /// <summary>
    /// The same guard one tier up. Losing the last super admin would leave the system prompts
    /// uneditable by anyone, with no way back short of a database edit.
    /// </summary>
    private static async Task EnsureNotLastSuperAdminAsync(SagaDbContext db, Guid userId, CancellationToken ct)
    {
        var others = await db.Users.CountAsync(u => u.IsSuperAdmin && !u.IsDeleted && u.Id != userId, ct);
        if (others == 0)
            throw new InvalidOperationException("At least one super admin must remain.");
    }
}
