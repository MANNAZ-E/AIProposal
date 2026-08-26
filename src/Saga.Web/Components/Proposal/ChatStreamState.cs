namespace Saga.Web.Components.Proposal;

/// <summary>
/// Answers still arriving, keyed by chat, held for the life of the circuit.
/// </summary>
/// <remarks>
/// These buffers used to be fields on <c>ChatSection</c>, which was fine while switching chats was
/// a change of page state. Now that the open chat is part of the URL, every switch is a navigation
/// that destroys and rebuilds the component — so the buffers live out here instead, and the thing
/// they were written for still holds: switching chats mid-answer keeps filling the right buffer and
/// the right row keeps spinning. It also buys something that never worked before, because the
/// component died the same way: an answer keeps arriving while you are off in another tab.
/// Same lifetime and the same reasoning as <see cref="ChatDraftState"/>.
/// </remarks>
public sealed class ChatStreamState
{
    /// <summary>The unsaved chat buffers under Guid.Empty until its first answer names it.</summary>
    private readonly Dictionary<(Guid Proposal, Guid Chat), string> _text = [];

    /// <summary>
    /// Raised on every delta so whichever <c>ChatSection</c> is on screen re-renders. The argument
    /// is the chat that just finished, or null for a delta: by the time an answer lands the
    /// component that asked for it is usually gone, and somebody still has to reload the messages
    /// the service saved.
    /// </summary>
    public event Action<Guid?>? Changed;

    /// <summary>The text so far, or null if this chat is not answering.</summary>
    public string? Get(Guid proposalId, Guid chatId)
        => _text.TryGetValue((proposalId, chatId), out var text) ? text : null;

    public bool IsAnswering(Guid proposalId, Guid chatId) => _text.ContainsKey((proposalId, chatId));

    /// <summary>Which of this proposal's chats are answering, for the list's spinners.</summary>
    public HashSet<Guid> AnsweringIds(Guid proposalId)
        => _text.Keys.Where(k => k.Proposal == proposalId).Select(k => k.Chat).ToHashSet();

    public void Start(Guid proposalId, Guid chatId)
    {
        _text[(proposalId, chatId)] = "";
        Changed?.Invoke(null);
    }

    public void Append(Guid proposalId, Guid chatId, string delta)
    {
        var key = (proposalId, chatId);
        _text[key] = _text.TryGetValue(key, out var text) ? text + delta : delta;
        Changed?.Invoke(null);
    }

    /// <summary>
    /// The draft's Guid.Empty buffer becomes the real chat's, before the answer starts arriving —
    /// and frees the draft slot, so pressing New again cannot duplicate the chat.
    /// </summary>
    public void Rekey(Guid proposalId, Guid from, Guid to)
    {
        if (from == to) return;
        if (_text.Remove((proposalId, from), out var buffered)) _text[(proposalId, to)] = buffered;
        Changed?.Invoke(null);
    }

    /// <summary>Done, or failed: either way the buffer goes and the saved messages are the truth.</summary>
    public void Finish(Guid proposalId, Guid chatId)
    {
        _text.Remove((proposalId, chatId));
        Changed?.Invoke(chatId);
    }
}
