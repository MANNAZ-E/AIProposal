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

    /// <summary>An ordinary thread to post into, since nothing creates one by itself any more.</summary>
    private static Task<Guid> ThreadAsync(TeamChatService chat, Guid proposalId, Guid userId)
        => chat.StartThreadAsync(proposalId, userId, "Kickoff");

    [Fact]
    public async Task A_proposal_starts_with_no_threads()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        // Listing creates nothing: the section opens on an unstarted draft, so a proposal nobody
        // has posted to has no conversation and no unread badge.
        Assert.Empty(await chat.ListThreadsAsync(proposalId, elv));
        Assert.Empty(await chat.ListThreadsAsync(proposalId, sda));
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));
    }

    [Fact]
    public async Task A_reader_can_post_and_read()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.PostAsync(threadId, sda, "Reading only, but I still have opinions.");

        // Both sides see it: every team thread is shared by definition.
        var mine = await chat.ListAsync(threadId, sda);
        var theirs = await chat.ListAsync(threadId, elv);
        Assert.Equal("Reading only, but I still have opinions.", mine[^1].Text);
        Assert.Equal(sda, theirs[^1].AuthorId);
    }

    [Fact]
    public async Task A_reader_can_start_a_thread_and_it_is_named_from_the_first_message()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        var threadId = await chat.StartThreadAsync(proposalId, sda, "Pricing for the Q3 renewal?");

        var thread = (await chat.ListThreadsAsync(proposalId, elv)).Single(t => t.Id == threadId);
        // Trailing punctuation is dropped: the title is a label, not a sentence.
        Assert.Equal("Pricing for the Q3 renewal", thread.Title);
        Assert.Equal(sda, thread.CreatedById);
        // A thread that exists always has its first message in it.
        Assert.Equal("Pricing for the Q3 renewal?", Assert.Single(await chat.ListAsync(threadId, elv)).Text);
    }

    [Fact]
    public async Task Threads_are_listed_newest_message_first()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat();
        var older = await chat.StartThreadAsync(proposalId, elv, "Started first");
        // The clock behind LastMessageAt ticks in tens of milliseconds; without the pause the two
        // threads can share a timestamp and the order stops being a fact about the code.
        await Task.Delay(25);
        var newer = await chat.StartThreadAsync(proposalId, elv, "Started second");

        Assert.Equal([newer, older], (await chat.ListThreadsAsync(proposalId, elv)).Select(t => t.Id));

        // Recency is the newest message, not the start: replying lifts an old thread back to the top.
        await Task.Delay(25);
        await chat.PostAsync(older, elv, "Reviving this one");
        Assert.Equal([older, newer], (await chat.ListThreadsAsync(proposalId, elv)).Select(t => t.Id));
    }

    [Fact]
    public async Task A_non_member_is_refused()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var outsider = (await _users.GetOrCreateAsync("outsider@mannaz.com", "Outsider", null)).Id;
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.PostAsync(threadId, outsider, "Let me in"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.ListAsync(threadId, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.ListThreadsAsync(proposalId, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.StartThreadAsync(proposalId, outsider, "Hello?"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => chat.UnreadCountAsync(proposalId, outsider));
    }

    [Fact]
    public async Task A_mention_of_a_team_member_is_stored_with_its_offsets()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.PostAsync(threadId, elv, "Over to you @Stefanie Baptiste, deadline is Friday.");

        var message = (await chat.ListAsync(threadId, elv))[^1];
        var mention = Assert.Single(message.Mentions);
        Assert.Equal(sda, mention.UserId);
        Assert.Equal(12, mention.Start);
        Assert.Equal("@Stefanie Baptiste".Length, mention.Length);
        // The offsets are what makes rendering a pure splice rather than a second scan.
        Assert.Equal("@Stefanie Baptiste", message.Text.Substring(mention.Start, mention.Length));
    }

    [Fact]
    public async Task A_first_message_that_starts_a_thread_still_resolves_its_mentions()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        var threadId = await chat.StartThreadAsync(proposalId, elv, "@Stefanie Baptiste can you take this one?");

        var mention = Assert.Single(Assert.Single(await chat.ListAsync(threadId, elv)).Mentions);
        Assert.Equal(sda, mention.UserId);
    }

    [Fact]
    public async Task A_mention_of_someone_not_on_the_team_writes_no_row()
    {
        var (elv, _, proposalId) = await SetupAsync();
        await _users.GetOrCreateAsync("bystander@mannaz.com", "Bystander", null);
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.PostAsync(threadId, elv, "@Bystander @nobody @Stefanie Baptiste");

        var message = (await chat.ListAsync(threadId, elv))[^1];
        // Only the teammate resolves: a user who exists but is not on this bid team does not.
        var mention = Assert.Single(message.Mentions);
        Assert.Equal("@Stefanie Baptiste", message.Text.Substring(mention.Start, mention.Length));
    }

    [Fact]
    public async Task Mentioning_yourself_is_not_a_mention()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.PostAsync(threadId, elv, "Note to self, @Emil Lindeløv Vestergaard: book the room.");

        Assert.Empty((await chat.ListAsync(threadId, elv))[^1].Mentions);
    }

    [Fact]
    public async Task Unread_counts_other_peoples_messages_until_you_read_them()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await chat.StartThreadAsync(proposalId, elv, "Mine");
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));

        await chat.PostAsync(threadId, sda, "Theirs");
        await chat.PostAsync(threadId, sda, "And another");
        Assert.Equal(2, await chat.UnreadCountAsync(proposalId, elv));
        // Posting moves your own watermark, so the poster is never behind on their own thread.
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, sda));

        await chat.MarkSeenAsync(threadId, elv);
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));
    }

    [Fact]
    public async Task Reading_one_thread_does_not_clear_another()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var mine = await ThreadAsync(chat, proposalId, elv);

        var started = await chat.StartThreadAsync(proposalId, sda, "About the site visit");
        await chat.PostAsync(mine, sda, "And a separate thing");

        // The badge is one number across the proposal; the dots are per thread.
        Assert.Equal(2, await chat.UnreadCountAsync(proposalId, elv));
        var before = await chat.ListThreadsAsync(proposalId, elv);
        Assert.True(before.Single(t => t.Id == started).HasUnread);
        Assert.True(before.Single(t => t.Id == mine).HasUnread);

        await chat.MarkSeenAsync(started, elv);

        Assert.Equal(1, await chat.UnreadCountAsync(proposalId, elv));
        var after = await chat.ListThreadsAsync(proposalId, elv);
        Assert.False(after.Single(t => t.Id == started).HasUnread);
        Assert.True(after.Single(t => t.Id == mine).HasUnread);
    }

    [Fact]
    public async Task A_thread_you_started_is_unread_to_everybody_but_you()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        await chat.StartThreadAsync(proposalId, elv, "Starting this myself");

        // Starting a thread moves your own watermark with it, which is what lets the dot be
        // decided by the watermark alone now that no thread can exist without a message in it.
        Assert.False(Assert.Single(await chat.ListThreadsAsync(proposalId, elv)).HasUnread);
        Assert.Equal(0, await chat.UnreadCountAsync(proposalId, elv));

        Assert.True(Assert.Single(await chat.ListThreadsAsync(proposalId, sda)).HasUnread);
        Assert.Equal(1, await chat.UnreadCountAsync(proposalId, sda));
    }

    [Fact]
    public async Task Marking_seen_twice_does_not_write_twice()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);
        await chat.PostAsync(threadId, sda, "Hello");

        await chat.MarkSeenAsync(threadId, elv);
        await using var check = db.CreateDbContext();
        var first = await check.TeamChatSeen
            .SingleAsync(s => s.TeamThreadId == threadId && s.UserId == elv);
        var watermark = first.LastSeenAt;

        // A component calls this per load; the no-op path is what makes that free.
        await chat.MarkSeenAsync(threadId, elv);
        await using var recheck = db.CreateDbContext();
        var again = await recheck.TeamChatSeen
            .SingleAsync(s => s.TeamThreadId == threadId && s.UserId == elv);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(watermark, again.LastSeenAt);
    }

    [Fact]
    public async Task The_last_thread_can_be_deleted_and_leaves_the_proposal_with_none()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var chat = NewChat();
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.RenameAsync(threadId, elv, "Everything else");
        Assert.Equal("Everything else",
            Assert.Single(await chat.ListThreadsAsync(proposalId, elv)).Title);

        // Nothing is pinned open: the section falls back to a draft rather than to a kept thread.
        await chat.DeleteAsync(threadId, elv);
        Assert.Empty(await chat.ListThreadsAsync(proposalId, elv));
    }

    [Fact]
    public async Task Deleting_a_thread_takes_its_messages_and_read_marks_with_it()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();

        var threadId = await chat.StartThreadAsync(proposalId, sda, "Something @elv should see");
        await chat.MarkSeenAsync(threadId, elv);

        await chat.DeleteAsync(threadId, sda);

        await using var check = db.CreateDbContext();
        Assert.False(await check.TeamMessages.AnyAsync(m => m.TeamThreadId == threadId));
        Assert.False(await check.TeamChatSeen.AnyAsync(s => s.TeamThreadId == threadId));
        Assert.DoesNotContain(await chat.ListThreadsAsync(proposalId, elv), t => t.Id == threadId);
    }

    [Fact]
    public async Task Only_the_person_who_started_a_thread_or_the_proposal_owner_may_manage_it()
    {
        var (elv, sda, proposalId) = await SetupAsync();
        var chat = NewChat();
        var mine = await chat.StartThreadAsync(proposalId, elv, "Owner's thread");
        var theirs = await chat.StartThreadAsync(proposalId, sda, "Reader's thread");

        // A reader may not touch somebody else's thread...
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.RenameAsync(mine, sda, "Nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.DeleteAsync(mine, sda));

        // ...but owns their own, and the proposal owner can clean up either.
        await chat.RenameAsync(theirs, sda, "Renamed by its author");
        await chat.DeleteAsync(theirs, elv);

        var threads = await chat.ListThreadsAsync(proposalId, sda);
        var flags = threads.Single(t => t.Id == mine);
        Assert.False(flags.CanRename);
        Assert.False(flags.CanDelete);
        Assert.DoesNotContain(threads, t => t.Id == theirs);
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
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.PostAsync(threadId, elv, "   "));
        // An empty first message would leave a thread with nothing to be named after.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.StartThreadAsync(proposalId, elv, "   "));

        await chat.PostAsync(threadId, elv, new string('x', 5000));
        Assert.Equal(4000, (await chat.ListAsync(threadId, elv))[^1].Text.Length);
    }

    [Fact]
    public async Task Posting_publishes_the_proposal_and_the_thread_to_open_circuits()
    {
        var (elv, _, proposalId) = await SetupAsync();
        var notifier = new TeamChatNotifier();
        var seen = new List<(Guid Proposal, Guid Thread)>();
        notifier.Posted += (p, t) => seen.Add((p, t));
        var chat = new TeamChatService(db, notifier);
        var threadId = await ThreadAsync(chat, proposalId, elv);

        await chat.PostAsync(threadId, elv, "Anyone there?");
        var started = await chat.StartThreadAsync(proposalId, elv, "A second conversation");

        // The thread id is what lets a listening circuit tell "reload the list" from
        // "reload the transcript I am looking at". Starting one publishes too — the first entry
        // is the thread the helper started.
        Assert.Equal(
            [(proposalId, threadId), (proposalId, threadId), (proposalId, started)], seen);
    }

    /// <summary>
    /// A tab closed while someone was typing throws out of its handler. That must neither fail the
    /// post that woke it nor stop the notification reaching the circuits behind it.
    /// </summary>
    [Fact]
    public void A_faulting_subscriber_neither_throws_nor_stops_the_others()
    {
        var notifier = new TeamChatNotifier();
        var reached = new List<int>();
        notifier.Posted += (_, _) => reached.Add(1);
        notifier.Posted += (_, _) => throw new ObjectDisposedException("circuit");
        notifier.Posted += (_, _) => reached.Add(3);

        notifier.Publish(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal([1, 3], reached);
    }
}
