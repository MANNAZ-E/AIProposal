using Microsoft.Extensions.Logging;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Tells every open circuit that a paid call has been recorded against a proposal, so the running
/// spend figure in the app bar moves while the money is being spent rather than at the next tab
/// click. Same mechanism as <see cref="TeamChatNotifier"/>: Blazor Server already holds a circuit
/// per open tab, so a singleton event is the whole live update — no timer and no polling.
///
/// It carries only the proposal id, never the cost. The listener re-reads the total itself, because
/// a pushed delta would double-count against a refresh already in flight, would skip the reader's
/// own access check, and would put a second copy of a number that has one home in the database.
///
/// <see cref="Publish"/> cannot throw. It is called from the usage decorators, whose one hard rule
/// is that metering never breaks the call it was measuring, and the subscribers are other people's
/// circuits — a component that faults must not take the generation down with it.
///
/// Single-process by design, like its sibling: if Saga is ever scaled out, live push degrades to
/// per-instance and the fix is a backplane behind this same call.
/// </summary>
public class AiUsageNotifier(ILogger<AiUsageNotifier>? logger = null)
{
    public event Action<Guid>? Recorded;

    public void Publish(Guid proposalId)
    {
        if (Recorded is not { } recorded) return;

        // Invoked one handler at a time rather than through the multicast delegate directly: a
        // synchronous throw from one circuit must not stop the notification reaching every other
        // circuit still subscribed after it.
        foreach (var handler in recorded.GetInvocationList())
        {
            try
            {
                ((Action<Guid>)handler)(proposalId);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to publish AI usage for proposal {ProposalId}.", proposalId);
            }
        }
    }
}
