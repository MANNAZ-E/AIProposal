using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class TeamChatTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);

    private TeamChatService NewChat() => new(db, new TeamChatNotifier());

    /// <summary>elv owns the proposal; sda is on it as a Reader — the role the requirement is about.</summary>
    private async Task<(Guid ElvId, Guid SdaId, Guid ProposalId)> SetupAsync(
        ProposalRole sdaRole = ProposalRole.Reader)
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var sda = (await _users.FindByEmailAsync("sda@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "P", "C", null, OutputFormat.PowerPoint);
        await _proposals.ShareAsync(proposalId, elv, "sda@mannaz.com", sdaRole);
        return (elv, sda, proposalId);
    }

    [Fact]
    public async Task A_reader_can_post_and_read()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        await chat.PostAsync(proposalId, sda, "Reading only, but I still have opinions.");

        // Both sides see it: the team thread is shared by definition.
        var mine = await chat.ListAsync(proposalId, sda);
        var theirs = await chat.ListAsync(proposalId, elv);
        Assert.Equal("Reading only, but I still have opinions.", Assert.Single(mine).Text);
        Assert.Equal(sda, Assert.Single(theirs).AuthorId);
    }

    [Fact]
    public async Task A_non_member_is_refused()
    {
        var (_, _, proposalId) = await SetupAsync();
        var outsider = (await _users.GetOrCreateAsync("outsider@mannaz.com", "Outsider", null)).Id;
        var chat = NewChat();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.PostAsync(proposalId, outsider, "Let me in"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.ListAsync(proposalId, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.UnreadCountAsync(proposalId, outsider));
    }

    [Fact]
    public async Task A_mention_of_a_team_member_is_stored_with_its_offsets()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        await chat.PostAsync(proposalId, elv, "Over to you @sda, deadline is Friday.");

        var message = Assert.Single(await chat.ListAsync(proposalId, elv));
        var mention = Assert.Single(message.Mentions);
        Assert.Equal(sda, mention.UserId);
        Assert.Equal(12, mention.Start);
        Assert.Equal("@sda".Length, mention.Length);
        // The offsets are what makes rendering a pure splice rather than a second scan.
        Assert.Equal("@sda", message.Text.Substring(mention.Start, mention.Length));
    }

    [Fact]
    public async Task A_mention_of_someone_not_on_the_team_writes_no_row()
    {
        var (elv, _, proposalId) = await SetupAsync();
        await _users.GetOrCreateAsync("bystander@mannaz.com", "Bystander", null);
        var chat = NewChat();

        await chat.PostAsync(proposalId, elv, "@Bystander @nobody @sda");

        var message = Assert.Single(await chat.ListAsync(proposalId, elv));
        // Only the teammate resolves: a user who exists but is not on this bid team does not.
        var mention = Assert.Single(message.Mentions);
        Assert.Equal("@sda", message.Text.Substring(mention.Start, mention.Length));
    }

    [Fact]
    public async Task Mentioning_yourself_is_not_a_mention()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat();

        await chat.PostAsync(proposalId, elv, "Note to self, @Emil: book the room.");

        Assert.Empty(Assert.Single(await chat.ListAsync(proposalId, elv)).Mentions);
    }

    [Fact]
    public async Task Unread_counts_other_peoples_messages_until_you_read_them()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        await chat.PostAsync(proposalId, elv, "Mine");
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));

        await chat.PostAsync(proposalId, sda, "Theirs");
        await chat.PostAsync(proposalId, sda, "And another");
        Assert.Equal(2, await chat.UnreadCountAsync(proposalId, elv));
        // Posting moves your own watermark, so the poster is never behind on their own thread.
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, sda));

        await chat.MarkSeenAsync(proposalId, elv);
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));
    }

    [Fact]
    public async Task Marking_seen_twice_does_not_write_twice()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        await chat.PostAsync(proposalId, sda, "Hello");

        await chat.MarkSeenAsync(proposalId, elv);
        await using var check = db.CreateDbContext();
        var first = await check.TeamChatSeen
            .SingleAsync(s => s.ProposalId == proposalId && s.UserId == elv);
        var watermark = first.LastSeenAt;

        // A component calls this per load; the no-op path is what makes that free.
        await chat.MarkSeenAsync(proposalId, elv);
        await using var recheck = db.CreateDbContext();
        var again = await recheck.TeamChatSeen
            .SingleAsync(s => s.ProposalId == proposalId && s.UserId == elv);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(watermark, again.LastSeenAt);
    }

    [Fact]
    public async Task Colour_slots_are_stable_across_calls_and_across_viewers()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        var first = await chat.MembersAsync(proposalId, elv);
        var second = await chat.MembersAsync(proposalId, elv);
        var asSda = await chat.MembersAsync(proposalId, sda);

        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
        // Slots come from team position, not from who is looking, so a person is one colour
        // in everybody's window.
        Assert.Equal(first, asSda);
        Assert.Equal(new[] { 0, 1 }, first.Select(m => m.ColourSlot).ToArray());
    }

    [Fact]
    public async Task An_empty_message_is_refused_and_a_long_one_is_truncated()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.PostAsync(proposalId, elv, "   "));

        await chat.PostAsync(proposalId, elv, new string('x', 5000));
        Assert.Equal(4000, Assert.Single(await chat.ListAsync(proposalId, elv)).Text.Length);
    }

    [Fact]
    public async Task Posting_publishes_the_proposal_to_open_circuits()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var notifier = new TeamChatNotifier();
        var seen = new List<Guid>();
        notifier.Posted += id => seen.Add(id);

        await new TeamChatService(db, notifier).PostAsync(proposalId, elv, "Anyone there?");

        Assert.Equal(proposalId, Assert.Single(seen));
    }
}
