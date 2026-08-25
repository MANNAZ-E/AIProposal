namespace Saga.Web.Components.Proposal;

/// <summary>Which composer a draft was typed into; both start their unsaved chat at Guid.Empty.</summary>
public enum ChatDraftKind
{
    AiChat,
    TeamThread,
}

/// <summary>
/// Messages typed into a chat composer but not sent yet, held for the life of the circuit.
/// </summary>
/// <remarks>
/// The workspace sections are page state, not routes, so <c>ChatSection</c> is destroyed and
/// rebuilt every time someone leaves the AI Chat tab — including when they follow the
/// "Go to materials" link out of an empty material picker, which is precisely when there is
/// half a question on screen. Keeping the text here rather than in the component means it
/// survives that round trip, and being scoped it dies with the browser session, which is all
/// an unsent draft is worth. <c>BidTeamChatSection</c> uses it for the same reason.
/// </remarks>
public sealed class ChatDraftState
{
    /// <summary>Keyed by composer and chat, so the draft chat and each started chat keep their
    /// own text — and the two sections' unsaved drafts, both Guid.Empty, stay apart.</summary>
    private readonly Dictionary<(Guid Proposal, ChatDraftKind Kind, Guid Chat), string> _drafts = [];

    public string Get(Guid proposalId, ChatDraftKind kind, Guid chatId)
        => _drafts.TryGetValue((proposalId, kind, chatId), out var text) ? text : "";

    public void Set(Guid proposalId, ChatDraftKind kind, Guid chatId, string text)
    {
        if (string.IsNullOrEmpty(text)) _drafts.Remove((proposalId, kind, chatId));
        else _drafts[(proposalId, kind, chatId)] = text;
    }
}
