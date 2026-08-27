using Microsoft.Extensions.Logging;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Tells every open circuit that a bid team thread has a new message. Blazor Server already holds
/// a circuit per open tab, so a singleton event is all the live update needs — no timer and no
/// per-tab database round-trip.
///
/// It carries both ids because the two listeners want different things: the thread list refreshes
/// on any thread of the proposal (times and unread dots move), while the transcript reloads only
/// when the thread on screen is the one that changed.
///
/// <see cref="Publish"/> cannot throw, for the same reason <see cref="AiUsageNotifier.Publish"/>
/// cannot: the subscribers are other people's circuits, and a component that faults must neither
/// take down the post that woke it nor stop the notification reaching the circuits behind it.
///
/// Single-process by design: if Saga is ever scaled out, live push degrades to per-instance and
/// the fix is a backplane behind this same call, not a redesign of the components.
/// </summary>
public class TeamChatNotifier(ILogger<TeamChatNotifier>? logger = null)
{
    public event Action<Guid, Guid>? Posted;

    public void Publish(Guid proposalId, Guid threadId)
    {
        if (Posted is not { } posted) return;

        // Invoked one handler at a time rather than through the multicast delegate directly: a
        // synchronous throw from one circuit must not stop the notification reaching every other
        // circuit still subscribed after it.
        foreach (var handler in posted.GetInvocationList())
        {
            try
            {
                ((Action<Guid, Guid>)handler)(proposalId, threadId);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to publish a team message on thread {ThreadId} " +
                    "of proposal {ProposalId}.", threadId, proposalId);
            }
        }
    }
}
