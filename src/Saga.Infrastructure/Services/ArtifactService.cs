using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Core.Pipeline;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public class ArtifactService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<List<Artifact>> GetAllAsync(Guid proposalId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.Artifacts.Where(a => a.ProposalId == proposalId).ToListAsync(ct);
    }

    public async Task<Artifact?> GetAsync(Guid proposalId, ArtifactType type, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.Artifacts.FirstOrDefaultAsync(a => a.ProposalId == proposalId && a.Type == type, ct);
    }

    /// <summary>
    /// Saves a manual edit. Optimistic concurrency: if someone else changed the artifact since
    /// <paramref name="expectedRowVersion"/> was read, a <see cref="ConcurrencyConflictException"/>
    /// carries their version back to the caller.
    /// </summary>
    public async Task<Artifact> SaveEditAsync(Guid proposalId, ArtifactType type, Guid userId,
        string? contentMarkdown, string? contentJson, byte[] expectedRowVersion, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);

        var artifact = await db.Artifacts.FirstOrDefaultAsync(a => a.ProposalId == proposalId && a.Type == type, ct)
            ?? throw new InvalidOperationException("The artifact does not exist yet.");

        if (!artifact.RowVersion.SequenceEqual(expectedRowVersion))
            throw Conflict(artifact);

        var now = DateTimeOffset.UtcNow;
        artifact.ContentMarkdown = contentMarkdown;
        artifact.ContentJson = contentJson;
        artifact.Status = ArtifactStatus.Edited;
        artifact.IsStale = false;
        artifact.UpdatedAt = now;
        db.Entry(artifact).Property(a => a.RowVersion).OriginalValue = expectedRowVersion;

        db.ArtifactVersions.Add(Snapshot(artifact, VersionOrigin.Edited, userId, now));
        await MarkDownstreamStaleAsync(db, proposalId, type, ct);
        await TouchProposalAsync(db, proposalId, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await using var freshDb = await dbFactory.CreateDbContextAsync(ct);
            var fresh = await freshDb.Artifacts.FirstAsync(a => a.ProposalId == proposalId && a.Type == type, ct);
            throw Conflict(fresh);
        }
        return artifact;
    }

    public async Task SetLockedAsync(Guid proposalId, ArtifactType type, Guid userId, bool locked,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Editor, ct);
        var artifact = await db.Artifacts.FirstAsync(a => a.ProposalId == proposalId && a.Type == type, ct);
        artifact.IsLocked = locked;
        if (locked)
            artifact.IsStale = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ArtifactVersion>> GetVersionsAsync(Guid proposalId, ArtifactType type, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.ArtifactVersions
            .Include(v => v.CreatedBy)
            .Where(v => v.Artifact!.ProposalId == proposalId && v.Artifact.Type == type)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Artifact> RestoreVersionAsync(Guid versionId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.ArtifactVersions.Include(v => v.Artifact)
            .FirstAsync(v => v.Id == versionId, ct);
        var artifact = version.Artifact!;
        await ProposalService.EnsureRoleAsync(db, artifact.ProposalId, userId, ProposalRole.Editor, ct);

        if (artifact.IsLocked)
            throw new InvalidOperationException("The artifact is locked. Unlock it before restoring a version.");

        var now = DateTimeOffset.UtcNow;
        artifact.ContentMarkdown = version.ContentMarkdown;
        artifact.ContentJson = version.ContentJson;
        artifact.Status = ArtifactStatus.Edited;
        artifact.IsStale = false;
        artifact.UpdatedAt = now;

        db.ArtifactVersions.Add(Snapshot(artifact, VersionOrigin.Restored, userId, now));
        await MarkDownstreamStaleAsync(db, artifact.ProposalId, artifact.Type, ct);
        await TouchProposalAsync(db, artifact.ProposalId, now, ct);
        await db.SaveChangesAsync(ct);
        return artifact;
    }

    internal static ArtifactVersion Snapshot(Artifact artifact, VersionOrigin origin, Guid userId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifact.Id,
            ContentMarkdown = artifact.ContentMarkdown,
            ContentJson = artifact.ContentJson,
            Origin = origin,
            CreatedById = userId,
            CreatedAt = now,
        };

    /// <summary>Spec §19: a change makes downstream artifacts stale, except locked ones.</summary>
    internal static async Task MarkDownstreamStaleAsync(SagaDbContext db, Guid proposalId, ArtifactType changed,
        CancellationToken ct)
    {
        var downstream = ArtifactDependencies.DownstreamOf(changed);
        if (downstream.Count == 0) return;
        var artifacts = await db.Artifacts
            .Where(a => a.ProposalId == proposalId && downstream.Contains(a.Type)
                        && a.Status != ArtifactStatus.Empty && !a.IsLocked)
            .ToListAsync(ct);
        foreach (var artifact in artifacts)
            artifact.IsStale = true;
    }

    internal static async Task TouchProposalAsync(SagaDbContext db, Guid proposalId, DateTimeOffset now,
        CancellationToken ct)
    {
        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        proposal.UpdatedAt = now;
    }

    private static ConcurrencyConflictException Conflict(Artifact current)
        => new("Someone else changed this artifact while you were editing.",
            current.ContentMarkdown, current.ContentJson, current.RowVersion);
}
