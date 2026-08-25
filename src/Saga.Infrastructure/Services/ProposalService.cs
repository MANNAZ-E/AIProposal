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
        return await ListForUserAsync(db, userId, deleted: false, ct);
    }

    /// <summary>The recycle bin: soft-deleted proposals this user is a member of, newest deletion first.</summary>
    public async Task<List<ProposalListItem>> GetRecycleBinAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await ListForUserAsync(db, userId, deleted: true, ct);
        return items.OrderByDescending(i => i.DeletedAt).ToList();
    }

    private static Task<List<ProposalListItem>> ListForUserAsync(SagaDbContext db, Guid userId, bool deleted,
        CancellationToken ct)
        => db.ProposalMembers
            .Where(m => m.UserId == userId && m.Proposal!.IsDeleted == deleted)
            .OrderByDescending(m => m.Proposal!.CreatedAt)
            .Select(m => new ProposalListItem(
                m.ProposalId,
                m.Proposal!.Title,
                m.Proposal.ClientName,
                m.Proposal.Owner!.DisplayName,
                m.Role,
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
        return membership is null || membership.Proposal!.IsDeleted
            ? null
            : (membership.Proposal!, membership.Role);
    }

    public async Task ShareAsync(Guid proposalId, Guid actingUserId, string email, ProposalRole role,
        CancellationToken ct = default)
    {
        if (role == ProposalRole.Owner)
            throw new InvalidOperationException("Ownership cannot be granted by sharing.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email.Trim().ToLower(), ct)
            ?? throw new InvalidOperationException($"No Mannaz user with email '{email}' exists in Saga.");

        var existing = await db.ProposalMembers
            .FirstOrDefaultAsync(m => m.ProposalId == proposalId && m.UserId == user.Id, ct);
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
                UserId = user.Id,
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
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);

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

    /// <summary>Restores a proposal from the recycle bin. Any team member can do this.</summary>
    public async Task RestoreAsync(Guid proposalId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Reader, ct);
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

    /// <summary>All roles at or above <paramref name="minimumRole"/> pass; others throw.</summary>
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
        var role = await db.ProposalMembers
            .Where(m => m.ProposalId == proposalId && m.UserId == userId)
            .Select(m => (ProposalRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (role is null || role < minimumRole)
            throw new UnauthorizedAccessException($"This action requires the {minimumRole} role on the proposal.");
        return role.Value;
    }
}
