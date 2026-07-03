using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Web.Auth;

/// <summary>
/// Scoped per Blazor circuit: maps the signed-in principal to the local user row once and caches it.
/// </summary>
public class CurrentUserService(AuthenticationStateProvider authStateProvider, UserService userService)
{
    private User? _user;

    public async Task<User> GetAsync(CancellationToken ct = default)
    {
        if (_user is not null) return _user;

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

        _user = await userService.GetOrCreateAsync(email, displayName, entraObjectId, ct);
        return _user;
    }
}
