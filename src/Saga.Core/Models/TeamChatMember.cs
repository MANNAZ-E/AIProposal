namespace Saga.Core.Models;

/// <summary>
/// Somebody on the bid team, as the team chat needs them: the picker lists them, the scanner
/// matches <c>@mentions</c> against them, and <paramref name="ColourSlot"/> decides which of the
/// three teammate colours their bubbles get.
/// </summary>
/// <param name="ColourSlot">
/// The member's index among the team ordered by <c>(AddedAt, UserId)</c>, mod 3. Derived from
/// team position rather than from the viewer, so a given person looks the same to everyone.
/// </param>
public record TeamChatMember(Guid UserId, string DisplayName, string Email, int ColourSlot);
