namespace Saga.Core.Models;

/// <summary>One candidate in the team-member picker's suggestion list.</summary>
public record UserSearchResult(Guid Id, string DisplayName, string Email);
