namespace Saga.Infrastructure.Services;

/// <summary>
/// Tells every open circuit that a proposal's team thread has a new message. Blazor Server
/// already holds a circuit per open tab, so a singleton event is all the live update needs —
/// no timer and no per-tab database round-trip.
///
/// Single-process by design: if Saga is ever scaled out, live push degrades to per-instance and
/// the fix is a backplane behind this same call, not a redesign of the components.
/// </summary>
public class TeamChatNotifier
{
    public event Action<Guid>? Posted;

    public void Publish(Guid proposalId) => Posted?.Invoke(proposalId);
}
