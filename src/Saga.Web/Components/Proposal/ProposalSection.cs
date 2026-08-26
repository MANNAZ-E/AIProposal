namespace Saga.Web.Components.Proposal;

/// <summary>
/// A tab of the proposal workspace. Public, and out here rather than private to
/// <c>ProposalPage</c>, because its slugs are a URL contract worth a test.
/// </summary>
public enum ProposalSection
{
    Material,
    Generate,
    Summary,
    ClientProfile,
    Requirements,
    Scoping,
    SolutionProposal,
    Structure,
    Content,
    Review,
    Chat,
    BidTeamChat,
    Export,
    Usage,
    People,
    Settings,
}

/// <summary>
/// The workspace's URLs. Every tab and every open conversation is addressable, so the browser's
/// Back button lands on the one you left instead of rebuilding the page at its defaults — which is
/// what it did while the tab lived in a private field.
/// </summary>
public static class ProposalSectionUrl
{
    /// <summary>
    /// The unsaved chat or thread, which has no id until its first message. A literal segment
    /// rather than a bare section URL, so "I am starting something new" is distinguishable from
    /// "open whichever conversation is newest".
    /// </summary>
    public const string New = "new";

    /// <summary>
    /// A section's URL segment. Kebab-case of the label on screen rather than of the enum member,
    /// which is where this parts company with <c>AdminPage</c> (<c>RecycleBin</c> → "recycle-bin"
    /// under the label "Recycling bin"): <c>BidTeamChat</c> and <c>People</c> are historical names
    /// for tabs that read "Team Chat" and "Team", and a URL somebody may have bookmarked should
    /// match what they clicked. The cost is that renaming a tab is now also a decision about
    /// whether to keep its old slug working.
    /// </summary>
    public static string Slug(ProposalSection section) => section switch
    {
        ProposalSection.Generate => "generate",
        ProposalSection.Summary => "summary",
        ProposalSection.ClientProfile => "client-profile",
        ProposalSection.Requirements => "requirements",
        ProposalSection.Scoping => "scoping",
        ProposalSection.SolutionProposal => "solution-proposal",
        ProposalSection.Structure => "structure",
        ProposalSection.Content => "content",
        ProposalSection.Review => "review",
        ProposalSection.Chat => "chat",
        ProposalSection.BidTeamChat => "team-chat",
        ProposalSection.Export => "export",
        ProposalSection.Usage => "usage",
        ProposalSection.People => "team",
        ProposalSection.Settings => "settings",
        _ => "material",
    };

    /// <summary>
    /// The section a URL names, or null for a slug nobody recognises — which the page turns into a
    /// redirect to the first tab rather than an error, the same way the admin and settings menus do.
    /// </summary>
    public static ProposalSection? Parse(string? slug) => slug?.ToLowerInvariant() switch
    {
        "material" => ProposalSection.Material,
        "generate" => ProposalSection.Generate,
        "summary" => ProposalSection.Summary,
        "client-profile" => ProposalSection.ClientProfile,
        "requirements" => ProposalSection.Requirements,
        "scoping" => ProposalSection.Scoping,
        "solution-proposal" => ProposalSection.SolutionProposal,
        "structure" => ProposalSection.Structure,
        "content" => ProposalSection.Content,
        "review" => ProposalSection.Review,
        "chat" => ProposalSection.Chat,
        "team-chat" => ProposalSection.BidTeamChat,
        "export" => ProposalSection.Export,
        "usage" => ProposalSection.Usage,
        "team" => ProposalSection.People,
        "settings" => ProposalSection.Settings,
        _ => null,
    };

    /// <summary>
    /// Where a tab lives, optionally down to the conversation open inside it. Every workspace URL
    /// is built here, so a slug is spelt in exactly one place.
    /// </summary>
    public static string For(Guid proposalId, ProposalSection section, string? sub = null)
        => sub is null
            ? $"/proposals/{proposalId}/{Slug(section)}"
            : $"/proposals/{proposalId}/{Slug(section)}/{sub}";
}
