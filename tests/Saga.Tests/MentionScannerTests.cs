using Saga.Core.Models;
using Saga.Core.Pipeline;

namespace Saga.Tests;

/// <summary>
/// The scanner is the source of truth for mentions — the composer's picker is only a shortcut,
/// so anything typed by hand has to resolve identically.
/// </summary>
public class MentionScannerTests
{
    private static readonly Guid EmilId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid EmiliaId = Guid.Parse("00000000-0000-0000-0000-0000000000e2");
    private static readonly Guid SdaId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private static readonly TeamChatMember Emil = new(EmilId, "Emil Larsen", "elv@mannaz.com", 0);
    private static readonly TeamChatMember Sda = new(SdaId, "sda", "sda@mannaz.com", 1);

    [Fact]
    public void Full_name_resolves()
    {
        var matches = MentionScanner.Scan("Can you look at this @Emil Larsen?", [Emil, Sda]);

        var match = Assert.Single(matches);
        Assert.Equal(EmilId, match.UserId);
        Assert.Equal(21, match.Start);
        Assert.Equal("@Emil Larsen".Length, match.Length);
    }

    [Fact]
    public void First_name_alone_resolves_when_it_is_the_display_name()
    {
        var emil = Emil with { DisplayName = "Emil" };

        var match = Assert.Single(MentionScanner.Scan("@Emil please", [emil, Sda]));
        Assert.Equal(EmilId, match.UserId);
        Assert.Equal("@Emil".Length, match.Length);
    }

    [Fact]
    public void Longest_candidate_wins()
    {
        // Both are on the team, and "@Emil Larsen" must not resolve as "@Emil" with a loose surname.
        var shortName = new TeamChatMember(SdaId, "Emil", "sda@mannaz.com", 1);

        var match = Assert.Single(MentionScanner.Scan("hi @Emil Larsen", [shortName, Emil]));
        Assert.Equal(EmilId, match.UserId);
        Assert.Equal("@Emil Larsen".Length, match.Length);
    }

    [Fact]
    public void Email_form_resolves()
    {
        var match = Assert.Single(MentionScanner.Scan("ask @sda@mannaz.com about it", [Emil, Sda]));
        Assert.Equal(SdaId, match.UserId);
        Assert.Equal("@sda@mannaz.com".Length, match.Length);
    }

    [Fact]
    public void A_longer_name_starting_with_a_candidate_is_not_that_candidate()
    {
        // Without the trailing-boundary check this would bold "@Emil" and leave "ia" behind it.
        var emil = Emil with { DisplayName = "Emil" };
        var emilia = new TeamChatMember(EmiliaId, "Emilia", "emilia@mannaz.com", 1);

        Assert.Empty(MentionScanner.Scan("@Emilia hello", [emil]));
        Assert.Equal(EmiliaId, Assert.Single(MentionScanner.Scan("@Emilia hello", [emil, emilia])).UserId);
    }

    [Fact]
    public void An_at_sign_mid_word_is_not_a_mention()
    {
        // A bare email address in prose mentions nobody.
        Assert.Empty(MentionScanner.Scan("write to x@sda@mannaz.com", [Sda]));
    }

    [Fact]
    public void An_unknown_name_yields_nothing()
    {
        Assert.Empty(MentionScanner.Scan("@nobody are you there", [Emil, Sda]));
    }

    [Fact]
    public void The_same_person_twice_yields_two_matches()
    {
        var matches = MentionScanner.Scan("@sda and again @sda", [Emil, Sda]);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(SdaId, m.UserId));
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(15, matches[1].Start);
    }

    [Fact]
    public void Matching_ignores_case()
    {
        Assert.Equal(EmilId, Assert.Single(MentionScanner.Scan("@emil larsen", [Emil])).UserId);
    }

    [Fact]
    public void An_empty_team_matches_nothing()
    {
        Assert.Empty(MentionScanner.Scan("@Emil Larsen", []));
    }
}
