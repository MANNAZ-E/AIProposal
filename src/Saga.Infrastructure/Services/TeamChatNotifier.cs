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
/// Single-process by design: if Saga is ever scaled out, live push degrades to per-instance and
/// the fix is a backplane behind this same call, not a redesign of the components.
/// </summary>
public class TeamChatNotifier
{
    public event Action<Guid, Guid>? Posted;

    public void Publish(Guid proposalId, Guid threadId) => Posted?.Invoke(proposalId, threadId);
}
