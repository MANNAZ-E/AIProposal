using Saga.Web.Components.Layout;

namespace Saga.Tests;

/// <summary>
/// Pins the app bar's spend figure to the proposal it belongs to. The failures this guards against
/// are all silent: a figure that shows the wrong bid's money, or one that never updates because the
/// no-op short-circuit swallowed the change. Nothing renders in a test, so nothing else would catch
/// either of them.
/// </summary>
public class AppHeaderStateTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    /// <summary>
    /// Two proposals can share a title, a client and an archive state — a duplicated bid, or two
    /// with no client name yet — and navigating between them reuses the one ProposalPage instance,
    /// so Clear never runs. Without the id in the comparison the second wears the first's spend.
    /// </summary>
    [Fact]
    public void Same_title_on_a_different_proposal_still_swaps_and_notifies()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, A);
        header.SetUsageCost(A, 1.5m);

        var notified = 0;
        header.Changed += () => notified++;
        header.Set("Rebid", "Acme", null, B);

        Assert.Equal(B, header.UsageProposalId);
        Assert.Null(header.UsageCostUsd);
        Assert.Equal(1, notified);
    }

    /// <summary>A tab switch republishes an identical title; the figure must not blink out.</summary>
    [Fact]
    public void Republishing_the_same_proposal_keeps_the_figure()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, A);
        header.SetUsageCost(A, 1.5m);

        header.Set("Rebid", "Acme", null, A);

        Assert.Equal(1.5m, header.UsageCostUsd);
    }

    /// <summary>
    /// Settings and Admin publish a title and never think about usage, which is exactly why the id
    /// is a parameter of Set: their call clears the figure without knowing it exists.
    /// </summary>
    [Fact]
    public void A_page_that_publishes_no_proposal_clears_the_figure()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, A);
        header.SetUsageCost(A, 1.5m);

        header.Set("Settings");

        Assert.Null(header.UsageProposalId);
        Assert.Null(header.UsageCostUsd);
    }

    [Fact]
    public void Clear_drops_the_figure_with_the_title()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, A);
        header.SetUsageCost(A, 1.5m);

        header.Clear();

        Assert.Null(header.Title);
        Assert.Null(header.UsageProposalId);
        Assert.Null(header.UsageCostUsd);
    }

    /// <summary>
    /// The handover, in the order Blazor actually runs it: the incoming page initialises during the
    /// render batch and the outgoing page's Dispose runs from the disposal queue afterwards. A plain
    /// Clear there wiped the title the new page had already set — leaving a proposal for /settings
    /// landed on a nameless bar.
    /// </summary>
    [Fact]
    public void A_page_leaving_does_not_clear_the_header_the_next_one_already_set()
    {
        var header = new AppHeaderState();
        var leaving = header.Set("Rebid", "Acme", null, A);

        header.Set("Settings");          // the incoming page, initialised first
        header.ClearIfCurrent(leaving);  // the outgoing page, disposed second

        Assert.Equal("Settings", header.Title);
        Assert.Null(header.UsageProposalId);
    }

    /// <summary>The opposite order — a page publishing after an await that really yielded.</summary>
    [Fact]
    public void The_other_order_leaves_the_incoming_header_standing_too()
    {
        var header = new AppHeaderState();
        var leaving = header.Set("Rebid", "Acme", null, A);

        header.ClearIfCurrent(leaving);  // disposed first
        header.Set("Admin");             // published second

        Assert.Equal("Admin", header.Title);
    }

    /// <summary>
    /// With nothing following it, a page leaving must still blank the bar — the dashboard publishes
    /// no title of its own and relies entirely on this.
    /// </summary>
    [Fact]
    public void A_page_leaving_with_nothing_after_it_clears_the_header()
    {
        var header = new AppHeaderState();
        var leaving = header.Set("Rebid", "Acme", null, A);

        header.ClearIfCurrent(leaving);

        Assert.Null(header.Title);
        Assert.Null(header.UsageProposalId);
    }

    /// <summary>
    /// A page republishes on every navigation, so only its most recent stamp is the live one — an
    /// older one must not clear a header that page itself has since replaced.
    /// </summary>
    [Fact]
    public void A_stale_stamp_from_the_same_page_does_not_clear()
    {
        var header = new AppHeaderState();
        var stale = header.Set("Rebid", "Acme", null, A);
        header.Set("Rebid renamed", "Acme", null, A);

        header.ClearIfCurrent(stale);

        Assert.Equal("Rebid renamed", header.Title);
    }

    /// <summary>
    /// The query is async, so a slow one can land after the user has moved on — and would otherwise
    /// show one bid's total under another's name.
    /// </summary>
    [Fact]
    public void A_cost_arriving_late_for_another_proposal_is_ignored()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, B);

        header.SetUsageCost(A, 99m);

        Assert.Null(header.UsageCostUsd);
    }

    /// <summary>
    /// The money is the only thing that changes while a generation runs. If it did not raise
    /// Changed, the bar would render the first figure and never move again.
    /// </summary>
    [Fact]
    public void A_changed_cost_notifies_and_an_unchanged_one_does_not()
    {
        var header = new AppHeaderState();
        header.Set("Rebid", "Acme", null, A);

        var notified = 0;
        header.Changed += () => notified++;

        header.SetUsageCost(A, 1.5m);
        Assert.Equal(1, notified);

        header.SetUsageCost(A, 1.5m);
        Assert.Equal(1, notified);

        header.SetUsageCost(A, 2m);
        Assert.Equal(2, notified);
    }
}
