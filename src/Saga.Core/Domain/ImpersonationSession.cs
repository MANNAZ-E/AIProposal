namespace Saga.Core.Domain;

/// <summary>
/// Audit record for an admin "view as" session. Written by <c>CurrentUserService</c> when an
/// impersonation starts/ends — never queried for authorization, only for the admin trail on
/// the /admin page.
/// </summary>
public class ImpersonationSession
{
    public Guid Id { get; set; }

    public Guid AdminUserId { get; set; }
    public User? AdminUser { get; set; }

    public Guid TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Null while the session is still open.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    public ImpersonationEndReason? EndReason { get; set; }
}
