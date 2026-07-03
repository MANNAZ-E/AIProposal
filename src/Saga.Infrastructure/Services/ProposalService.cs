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
        return await db.ProposalMembers
            .Where(m => m.UserId == userId)
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
                m.Proposal.UpdatedAt))
            .ToListAsync(ct);
    }

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
        return membership is null ? null : (membership.Proposal!, membership.Role);
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

    public async Task SetArchivedAsync(Guid proposalId, Guid actingUserId, bool archived, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        proposal.IsArchived = archived;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid proposalId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureRoleAsync(db, proposalId, actingUserId, ProposalRole.Owner, ct);
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        db.Proposals.Remove(proposal);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>All roles at or above <paramref name="minimumRole"/> pass; others throw.</summary>
    internal static async Task EnsureRoleAsync(SagaDbContext db, Guid proposalId, Guid userId,
        ProposalRole minimumRole, CancellationToken ct)
    {
        var role = await db.ProposalMembers
            .Where(m => m.ProposalId == proposalId && m.UserId == userId)
            .Select(m => (ProposalRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (role is null || role < minimumRole)
            throw new UnauthorizedAccessException($"This action requires the {minimumRole} role on the proposal.");
    }
}
