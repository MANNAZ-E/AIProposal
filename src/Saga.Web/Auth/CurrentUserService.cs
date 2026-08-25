using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Web.Auth;

/// <summary>
/// Scoped per Blazor circuit: maps the signed-in principal to the local user row once and caches it.
/// Also resolves this admin's impersonation state from the process-wide <see cref="ImpersonationState"/>
/// registry — while impersonating, <see cref="GetAsync"/> returns the target instead of the real
/// signed-in user, and every service reached through it (they all resolve the acting user from
/// here) acts on the target's behalf. The registry, not this instance, is the source of truth:
/// see its doc comment for why a circuit-local field cannot be.
/// </summary>
public class CurrentUserService(AuthenticationStateProvider authStateProvider, UserService userService,
    ImpersonationAuditService impersonationAudit, ImpersonationState impersonationState)
{
    private User? _realUser;
    private User? _effectiveUser;

    public bool IsImpersonating => _effectiveUser is not null && _realUser is not null && _effectiveUser.Id != _realUser.Id;
    public string? ImpersonatedDisplayName => IsImpersonating ? _effectiveUser!.DisplayName : null;

    public async Task<User> GetAsync(CancellationToken ct = default)
    {
        if (_effectiveUser is not null) return _effectiveUser;

        var real = await GetRealUserAsync(ct);
        _effectiveUser = real;

        if (impersonationState.TryGet(real.Id, out var targetId, out _))
        {
            var target = await userService.FindByIdAsync(targetId, ct);
            if (target is not null && !target.IsDeleted && !target.IsAdmin)
                _effectiveUser = target;
        }
        return _effectiveUser;
    }

    private async Task<User> GetRealUserAsync(CancellationToken ct = default)
    {
        if (_realUser is not null) return _realUser;

        var state = await authStateProvider.GetAuthenticationStateAsync();
        var principal = state.User;
        if (principal.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("No signed-in user.");

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("preferred_username")
            ?? throw new UnauthorizedAccessException("Signed-in user has no email claim.");
        var displayName = principal.FindFirstValue("name")
            ?? principal.Identity.Name
            ?? email;
        var entraObjectId = principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        var user = await userService.GetOrCreateAsync(email, displayName, entraObjectId, ct);
        if (user.IsDeleted)
            throw new AccessRevokedException("Your access to Saga has been revoked.");

        _realUser = user;
        return _realUser;
    }

    public async Task StartImpersonationAsync(Guid targetUserId, CancellationToken ct = default)
    {
        var admin = await GetRealUserAsync(ct);
        if (!admin.IsAdmin)
            throw new UnauthorizedAccessException("This action requires admin access.");
        if (impersonationState.TryGet(admin.Id, out _, out _))
            throw new InvalidOperationException("Already impersonating a user.");
        if (targetUserId == admin.Id)
            throw new InvalidOperationException("You cannot impersonate yourself.");

        var target = await userService.FindByIdAsync(targetUserId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (target.IsDeleted)
            throw new InvalidOperationException("Cannot impersonate a removed user.");
        if (target.IsAdmin)
            throw new InvalidOperationException("Cannot impersonate another admin.");

        var sessionId = await impersonationAudit.StartAsync(admin.Id, target.Id, ct);
        impersonationState.Set(admin.Id, target.Id, sessionId);
    }

    public async Task StopImpersonationAsync(CancellationToken ct = default)
    {
        var admin = await GetRealUserAsync(ct);
        if (impersonationState.TryRemove(admin.Id, out var sessionId))
            await impersonationAudit.EndAsync(sessionId, ImpersonationEndReason.StoppedByAdmin, ct);
    }
}
