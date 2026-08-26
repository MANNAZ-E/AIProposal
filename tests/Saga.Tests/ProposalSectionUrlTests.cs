using System.Text.RegularExpressions;
using Saga.Web.Components.Proposal;

namespace Saga.Tests;

/// <summary>
/// Pins the workspace's URL slugs. A section added without a <see cref="ProposalSectionUrl.Parse"/>
/// entry still renders a nav link, and clicking it silently bounces to Materials — the page treats
/// an unrecognised slug as "no section named" by design, so nothing else would notice. Two sections
/// sharing a slug fails the same quiet way, and a slug with a capital or a space works when clicked
/// and fails when typed.
/// </summary>
public class ProposalSectionUrlTests
{
    private static readonly ProposalSection[] AllSections = Enum.GetValues<ProposalSection>();

    public static TheoryData<ProposalSection> Sections()
    {
        var data = new TheoryData<ProposalSection>();
        foreach (var section in AllSections) data.Add(section);
        return data;
    }

    [Theory, MemberData(nameof(Sections))]
    public void Every_section_round_trips_through_its_slug(ProposalSection section)
        => Assert.Equal(section, ProposalSectionUrl.Parse(ProposalSectionUrl.Slug(section)));

    [Theory, MemberData(nameof(Sections))]
    public void Every_slug_is_typeable(ProposalSection section)
    {
        var slug = ProposalSectionUrl.Slug(section);
        Assert.Matches(new Regex("^[a-z][a-z0-9-]*$"), slug);
    }

    [Fact]
    public void No_two_sections_share_a_slug()
    {
        var slugs = AllSections.Select(ProposalSectionUrl.Slug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    /// <summary>The draft segment has to stay distinguishable from every section's own slug.</summary>
    [Fact]
    public void The_new_literal_is_not_also_a_section()
    {
        Assert.DoesNotContain(ProposalSectionUrl.New, AllSections.Select(ProposalSectionUrl.Slug));
        Assert.Null(ProposalSectionUrl.Parse(ProposalSectionUrl.New));
    }

    [Theory]
    [InlineData("CLIENT-PROFILE", ProposalSection.ClientProfile)]
    [InlineData("Team-Chat", ProposalSection.BidTeamChat)]
    [InlineData("Material", ProposalSection.Material)]
    public void Parse_ignores_case(string slug, ProposalSection expected)
        => Assert.Equal(expected, ProposalSectionUrl.Parse(slug));

    /// <summary>Exactly the cases the page turns into a redirect to the first tab.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("bid-team-chat")]
    [InlineData("people")]
    public void Parse_returns_null_for_anything_it_does_not_recognise(string? slug)
        => Assert.Null(ProposalSectionUrl.Parse(slug));

    [Fact]
    public void For_builds_the_tab_and_the_conversation_inside_it()
    {
        var id = Guid.Parse("2b1d5a3e-7c94-4f18-9a62-0d5e8c7b4f31");
        Assert.Equal($"/proposals/{id}/chat", ProposalSectionUrl.For(id, ProposalSection.Chat));
        Assert.Equal($"/proposals/{id}/chat/new",
            ProposalSectionUrl.For(id, ProposalSection.Chat, ProposalSectionUrl.New));
        Assert.Equal($"/proposals/{id}/team-chat/{id}",
            ProposalSectionUrl.For(id, ProposalSection.BidTeamChat, id.ToString()));
    }

    /// <summary>
    /// The two slugs that deliberately follow the tab's label rather than its enum member, which is
    /// where this parts company with the admin menu. Spelt out so the deviation is a decision on
    /// record rather than something to be "tidied up" later, breaking anybody's bookmark.
    /// </summary>
    [Fact]
    public void The_two_label_slugs_are_deliberate()
    {
        Assert.Equal("team-chat", ProposalSectionUrl.Slug(ProposalSection.BidTeamChat));
        Assert.Equal("team", ProposalSectionUrl.Slug(ProposalSection.People));
    }
}
