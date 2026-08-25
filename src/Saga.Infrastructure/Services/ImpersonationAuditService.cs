using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>One row in the admin impersonation-sessions table.</summary>
public record ImpersonationSessionRow(Guid Id, string AdminName, string TargetName,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, ImpersonationEndReason? EndReason);

/// <summary>
/// Writes and reads the impersonation audit trail. No authorization logic of its own —
/// <see cref="Saga.Web.Auth.CurrentUserService"/> is the only caller and has already checked
/// the acting admin before calling <see cref="StartAsync"/>.
/// </summary>
public class ImpersonationAuditService(IDbContextFactory<SagaDbContext> dbFactory)
{
    public async Task<Guid> StartAsync(Guid adminUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = new ImpersonationSession
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            TargetUserId = targetUserId,
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.ImpersonationSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    /// <summary>No-op if the session is already closed or missing.</summary>
    public async Task EndAsync(Guid sessionId, ImpersonationEndReason reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.ImpersonationSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.EndedAt is not null) return;
        session.EndedAt = DateTimeOffset.UtcNow;
        session.EndReason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Closes every session still open from a previous process lifetime. Active impersonation
    /// lives in the in-memory <c>ImpersonationState</c> registry, which does not survive a
    /// restart, so any row with no <see cref="ImpersonationSession.EndedAt"/> at startup was
    /// abandoned, not actually still active — called once during startup.
    /// </summary>
    public async Task CloseAbandonedSessionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var abandoned = await db.ImpersonationSessions.Where(s => s.EndedAt == null).ToListAsync(ct);
        if (abandoned.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var session in abandoned)
        {
            session.EndedAt = now;
            session.EndReason = ImpersonationEndReason.CircuitDisconnected;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ImpersonationSessionRow>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ImpersonationSessions
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new ImpersonationSessionRow(s.Id, s.AdminUser!.DisplayName, s.TargetUser!.DisplayName,
                s.StartedAt, s.EndedAt, s.EndReason))
            .ToListAsync(ct);
    }
}
