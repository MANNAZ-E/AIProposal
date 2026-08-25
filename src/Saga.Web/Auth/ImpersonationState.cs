using System.Collections.Concurrent;

namespace Saga.Web.Auth;

/// <summary>
/// Process-wide registry of active impersonation, keyed by the real admin's user id rather than
/// by circuit. AppBar, the persistent banner and each routed page each declare their own
/// @rendermode InteractiveServer, and their common ancestor (MainLayout) is not itself
/// interactive, so Blazor Server gives each one its own circuit — a circuit-scoped field on
/// CurrentUserService is therefore invisible across them. Every circuit for the same signed-in
/// admin consults this registry instead, so they agree on whether — and as whom — that admin is
/// currently browsing.
/// </summary>
public class ImpersonationState
{
    private readonly ConcurrentDictionary<Guid, (Guid TargetUserId, Guid SessionId)> _active = new();

    public bool TryGet(Guid adminUserId, out Guid targetUserId, out Guid sessionId)
    {
        if (_active.TryGetValue(adminUserId, out var entry))
        {
            (targetUserId, sessionId) = entry;
            return true;
        }
        targetUserId = default;
        sessionId = default;
        return false;
    }

    public void Set(Guid adminUserId, Guid targetUserId, Guid sessionId) => _active[adminUserId] = (targetUserId, sessionId);

    public bool TryRemove(Guid adminUserId, out Guid sessionId)
    {
        if (_active.TryRemove(adminUserId, out var entry))
        {
            sessionId = entry.SessionId;
            return true;
        }
        sessionId = default;
        return false;
    }
}
