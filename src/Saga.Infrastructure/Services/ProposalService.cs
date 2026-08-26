using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public class ProposalService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<List<ProposalListItem>> GetDashboardAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ListForUserAsync(db, userId, deleted: false, ownedOnly: false, ct);
    }

    /// <summary>
    /// Every live proposal in Saga, for the super-admin frontpage. The viewer's own role comes
    /// along where they have one, so the list can say which of them are theirs.
    /// </summary>
    public async Task<List<ProposalListItem>> GetAllAsync(Guid viewerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await IsSuperAdminAsync(db, viewerId, ct))
            throw new UnauthorizedAccessException("This action requires super admin access.");

        return await db.Proposals
            .Where(p => !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ProposalListItem(
                p.Id,
                p.Title,
                p.ClientName,
                p.Owner!.DisplayName,
                p.Members.Where(m => m.UserId == viewerId)
                    .Select(m => (ProposalRole?)m.Role).FirstOrDefault(),
                p.OwnerId == viewerId,
                p.IsArchived,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The recycle bin on the user's own Settings page: proposals they own and deleted, newest
    /// deletion first. Owner-scoped because only an owner can restore one — listing a teammate's
    /// deleted bid here would offer a button that cannot work. Everything else lives in the admin
    /// recycle bin, where a super admin can reach it.
    /// </summary>
    public async Task<List<ProposalListItem>> GetRecycleBinAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await ListForUserAsync(db, userId, deleted: true, ownedOnly: true, ct);
        return items.OrderByDescending(i => i.DeletedAt).ToList();
    }

    private static Task<List<ProposalListItem>> ListForUserAsync(SagaDbContext db, Guid userId, bool deleted,
        bool ownedOnly, CancellationToken ct)
        => db.ProposalMembers
            .Where(m => m.UserId == userId && m.Proposal!.IsDeleted == deleted
                        && (!ownedOnly || m.Proposal.OwnerId == userId))
            .OrderByDescending(m => m.Proposal!.CreatedAt)
            .Select(m => new ProposalListItem(
                m.ProposalId,
                m.Proposal!.Title,
                m.Proposal.ClientName,
                m.Proposal.Owner!.DisplayName,
                (ProposalRole?)m.Role,
                m.Proposal.OwnerId == userId,
                m.Proposal.IsArchived,
                m.Proposal.CreatedAt,
                m.Proposal.UpdatedAt,
                m.Proposal.DeletedAt))
            .ToListAsync(ct);

    public async Task<Guid> CreateAsync(Guid userId, string title, string clientName, string? description,
        OutputFormat outputFormat, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var proposal = new Proposal
        {
            Id = Guid.NewGuid(),
            Title = title.Trim(),
            ClientName = clientName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            OutputFormat = outputFormat,
            OwnerId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Proposals.Add(proposal);
        // Every proposal starts with the standard material categories; the team edits the list
        // from the Materials tab.
        db.DocumentTypes.AddRange(DocumentType.CreateDefaults(proposal.Id, now));
        db.ProposalMembers.Add(new ProposalMember
        {
            Id = Guid.NewGuid(),
            ProposalId = proposal.Id,
            UserId = userId,
            Role = ProposalRole.Owner,
            AddedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return proposal.Id;
    }

    public async Task<(Proposal Proposal, ProposalRole Role)?> GetForUserAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var membership = await db.ProposalMembers
            .Include(m => m.Proposal!).ThenInclude(p => p.Owner)
            .Include(m => m.Proposal!).ThenInclude(p => p.Members).ThenInclude(mm => mm.User)
            .FirstOrDefaultAsync(m => m.ProposalId == proposalId && m.UserId == userId, ct);
        // A deleted proposal reads as gone until someone restores it from the recycle bin.
        if (membership is not null)
            return membership.Proposal!.IsDeleted ? null : (membership.Proposal!, membership.Role);

        // No membership: a super admin still gets to look, as a Reader. Nothing is written, so
        // they never appear on the bid's team list — changing anything means joining it first.
        if (!await IsSuperAdminAsync(db, userId, ct)) return null;

        var proposal = await db.Proposals
            .Include(p => p.Owner)
            .Include(p => p.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(p => p.Id == proposalId && !p.IsDeleted, ct);
        return proposal is null ? null : (proposal, ProposalRole.Reader);
    }

    /// <summary>Adds a member by email address. Kept for callers that only have the address.</summary>
    public async Task ShareAsync(Guid proposalId, Guid actingUserId, string email, ProposalRole role,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureCanManageTeamAsync(db, proposalId, actingUserId, ct);

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == email.Trim().ToLower() && !u.IsDeleted, ct)
            ?? throw new InvalidOperationException($"No Mannaz user with email '{email}' exists in Saga.");

        await AddMemberCoreAsync(db, proposalId, user.Id, role, ct);
    }

    /// <summary>
    /// Adds a member the caller picked from the user search, so there is no address to mistype.
    /// </summary>
    public async Task AddMemberAsync(Guid proposalId, Guid actingUserId, Guid memberUserId,
        ProposalRole role, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureCanManageTeamAsync(db, proposalId, actingUserId, ct);

        if (!await db.Users.AnyAsync(u => u.Id == memberUserId && !u.IsDeleted, ct))
            throw new InvalidOperationException("That user no longer exists in Saga.");

        await AddMemberCoreAsync(db, proposalId, memberUserId, role, ct);
    }

    private static async Task AddMemberCoreAsync(SagaDbContext db, Guid proposalId, Guid userId,
        ProposalRole role, CancellationToken ct)
    {
        if (role == ProposalRole.Owner)
            throw new InvalidOperationException("Ownership cannot be granted by sharing.");

        var existing = await db.ProposalMembers
            .FirstOrDefaultAsync(m => m.ProposalId == proposalId && m.UserId == userId, ct);
        if (existing is not null)
        {
            if (existing.Role == ProposalRole.Owner) return;
            existing.Role = role;
        }
        else
        {
            db.ProposalMembers.Add(new ProposalMember
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                UserId = userId,
                Role = role,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid proposalId, Guid actingUserId, Guid memberUserId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureCanManageTeamAsync(db, proposalId, actingUserId, ct);

        var member = await db.ProposalMembers
            .FirstOrDefaultAsync(m => m.ProposalId == proposalId && m.UserId == memberUserId, ct);
        if (member is null || member.Role == ProposalRole.Owner) return;

        db.ProposalMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Renames the proposal and/or its client (Settings page). Owner only.</summary>
    public async Task UpdateDetailsAsync(Guid proposalId, Guid actingUserId, string title, string clientName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("The proposal needs a name.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        proposal.Title = title.Trim();
        proposal.ClientName = clientName.Trim();
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sets what the client-profile web research should look for. Blank values fall back to
    /// the proposal's own client name (research name) or to no website anchor.
    /// </summary>
    public async Task SetClientResearchAsync(Guid proposalId, Guid actingUserId, string? researchClientName,
        string? clientWebsite, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Editor, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);

        var name = string.IsNullOrWhiteSpace(researchClientName) ? null : researchClientName.Trim();
        var website = string.IsNullOrWhiteSpace(clientWebsite) ? null : clientWebsite.Trim();
        if (proposal.ResearchClientName == name && proposal.ClientWebsite == website) return;

        proposal.ResearchClientName = name;
        proposal.ClientWebsite = website;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Changes the output format. The structure keeps its titles, purposes and key messages;
    /// only which length column applies changes. Regenerating is left to the consultant.
    /// </summary>
    public async Task SetOutputFormatAsync(Guid proposalId, Guid actingUserId, OutputFormat format,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Editor, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        if (proposal.OutputFormat == format) return;

        proposal.OutputFormat = format;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(Guid proposalId, Guid actingUserId, bool archived, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        proposal.IsArchived = archived;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Soft delete: the proposal and everything under it stay in the database, flagged as deleted,
    /// and move to the recycle bin. Owner only.
    /// </summary>
    public async Task DeleteAsync(Guid proposalId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        if (proposal.IsDeleted) return;
        proposal.IsDeleted = true;
        proposal.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Restores a proposal from the owner's recycle bin. Owner only, to match deleting it: a
    /// reader who could undo the owner's deletion would be overruling them.
    /// </summary>
    public async Task RestoreAsync(Guid proposalId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        await RestoreCoreAsync(db, proposalId, ct);
    }

    /// <summary>Restores without a membership check — for the admin recycle bin.</summary>
    public async Task RestoreAsAdminAsync(Guid proposalId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await RestoreCoreAsync(db, proposalId, ct);
    }

    private static async Task RestoreCoreAsync(SagaDbContext db, Guid proposalId, CancellationToken ct)
    {
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        if (!proposal.IsDeleted) return;
        proposal.IsDeleted = false;
        proposal.DeletedAt = null;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Who may add and remove team members: the bid's Owner, an admin already working on the bid,
    /// or any super admin. Deliberately wider than the Owner check the rest of the service uses —
    /// it is the one door a super admin has, and the only way they can gain edit rights on a bid
    /// (by adding themselves, which the owner then sees on the team list).
    /// </summary>
    private static async Task EnsureCanManageTeamAsync(SagaDbContext db, Guid proposalId, Guid userId,
        CancellationToken ct)
    {
        var role = await db.ProposalMembers
            .Where(m => m.ProposalId == proposalId && m.UserId == userId)
            .Select(m => (ProposalRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (role == ProposalRole.Owner) return;

        var actor = await db.Users
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => new { u.IsAdmin, u.IsSuperAdmin })
            .FirstOrDefaultAsync(ct);
        if (actor is not null && (actor.IsSuperAdmin || actor.IsAdmin && role is not null)) return;

        throw new UnauthorizedAccessException("This action requires the Owner role on the proposal.");
    }

    private static Task<bool> IsSuperAdminAsync(SagaDbContext db, Guid userId, CancellationToken ct)
        => db.Users.AnyAsync(u => u.Id == userId && u.IsSuperAdmin && !u.IsDeleted, ct);

    /// <summary>
    /// May this user *look* at the proposal — every member, plus a super admin who is not on the
    /// bid. This is the only door the implicit oversight grant opens: the role guards below stay
    /// membership-only, so every write, down to posting in the bid team chat, still takes a real
    /// seat on the team. A super admin who wants one adds themselves, which the owner then sees.
    /// </summary>
    internal static async Task EnsureReadAccessAsync(SagaDbContext db, Guid proposalId, Guid userId,
        CancellationToken ct)
    {
        if (await MembershipRoleAsync(db, proposalId, userId, ct) is not null) return;
        if (await IsSuperAdminAsync(db, userId, ct)) return;
        throw new UnauthorizedAccessException("This action requires the Reader role on the proposal.");
    }

    /// <summary>
    /// The same read door, but it hands back a role for the finer decisions a read path makes
    /// downstream (whose chat this is, who may rename a thread). A super admin reading a bid they
    /// are not on comes back as a <see cref="ProposalRole.Reader"/> — which those checks already
    /// refuse, so oversight cannot leak into a write through the returned value.
    /// </summary>
    internal static async Task<ProposalRole> RequireRoleForReadAsync(SagaDbContext db, Guid proposalId,
        Guid userId, CancellationToken ct)
    {
        if (await MembershipRoleAsync(db, proposalId, userId, ct) is { } role) return role;
        if (await IsSuperAdminAsync(db, userId, ct)) return ProposalRole.Reader;
        throw new UnauthorizedAccessException("This action requires the Reader role on the proposal.");
    }

    /// <summary>True only for a real seat on the team — an implicit oversight read is not one.</summary>
    internal static async Task EnsureMemberAsync(SagaDbContext db, Guid proposalId, Guid userId,
        CancellationToken ct)
    {
        if (await MembershipRoleAsync(db, proposalId, userId, ct) is null)
            throw new UnauthorizedAccessException("Only the bid team can do this.");
    }

    private static Task<ProposalRole?> MembershipRoleAsync(SagaDbContext db, Guid proposalId,
        Guid userId, CancellationToken ct)
        => db.ProposalMembers
            .Where(m => m.ProposalId == proposalId && m.UserId == userId)
            .Select(m => (ProposalRole?)m.Role)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// All roles at or above <paramref name="minimumRole"/> pass; others throw. Membership only —
    /// see <see cref="EnsureReadAccessAsync"/> for why oversight does not come in here.
    /// </summary>
    internal static async Task EnsureRoleAsync(SagaDbContext db, Guid proposalId, Guid userId,
        ProposalRole minimumRole, CancellationToken ct)
        => await RequireRoleAsync(db, proposalId, userId, minimumRole, ct);

    /// <summary>
    /// The same guard, but it hands back the caller's role. Chat needs it: reading a shared chat
    /// takes Reader while posting into someone else's takes Editor, and both decisions should
    /// come from one membership lookup.
    /// </summary>
    internal static async Task<ProposalRole> RequireRoleAsync(SagaDbContext db, Guid proposalId,
        Guid userId, ProposalRole minimumRole, CancellationToken ct)
    {
        var role = await MembershipRoleAsync(db, proposalId, userId, ct);
        if (role is null || role < minimumRole)
            throw new UnauthorizedAccessException($"This action requires the {minimumRole} role on the proposal.");
        return role.Value;
    }
}
