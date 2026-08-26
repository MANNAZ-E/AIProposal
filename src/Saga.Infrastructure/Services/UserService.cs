using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

public class UserService(IDbContextFactory<SagaDbContext> dbFactory)
{
    /// <summary>
    /// Resolves the local user for a signed-in principal, creating or updating the row on first sight.
    /// Matches by Entra object id first, then by email (covers pre-seeded users signing in for the first time).
    /// </summary>
    public async Task<User> GetOrCreateAsync(string email, string displayName, string? entraObjectId,
        CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        User? user = null;
        if (entraObjectId is not null)
            user = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == entraObjectId, ct);
        user ??= await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = displayName,
                EntraObjectId = entraObjectId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return user;
        }

        if (user.EntraObjectId != entraObjectId && entraObjectId is not null
            || !string.IsNullOrWhiteSpace(displayName) && user.DisplayName != displayName)
        {
            user.EntraObjectId ??= entraObjectId;
            if (!string.IsNullOrWhiteSpace(displayName)) user.DisplayName = displayName;
            await db.SaveChangesAsync(ct);
        }
        return user;
    }

    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.FirstOrDefaultAsync(u => u.Email == email.Trim().ToLowerInvariant(), ct);
    }

    /// <summary>
    /// Candidates for the team-member picker: active users matching the term by name or address.
    /// A blank term returns the first <paramref name="limit"/> by name, so the list can open on
    /// focus rather than waiting for a keystroke.
    /// </summary>
    public async Task<List<UserSearchResult>> SearchActiveAsync(string? term, int limit = 8,
        CancellationToken ct = default)
    {
        term = term?.Trim() ?? "";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Users.Where(u => !u.IsDeleted);
        if (term.Length > 0)
            query = query.Where(u => u.DisplayName.Contains(term) || u.Email.Contains(term));

        return await query
            .OrderBy(u => u.DisplayName)
            .Take(limit)
            .Select(u => new UserSearchResult(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
    }

    public async Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    }
}
