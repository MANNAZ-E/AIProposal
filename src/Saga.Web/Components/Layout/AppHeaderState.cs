namespace Saga.Web.Components.Layout;

/// <summary>
/// Scoped per circuit: lets a page publish its own title into the app bar's top line, so the
/// proposal name and client are shown once at the top instead of on every workspace page.
/// </summary>
public class AppHeaderState
{
    public string? Title { get; private set; }
    public string? Subtitle { get; private set; }
    public string? Badge { get; private set; }

    /// <summary>
    /// The proposal the current page is working on, or null off the workspace — which is how the
    /// app bar knows whether to offer the running spend figure at all. It rides on the same call as
    /// the title on purpose: a setter of its own would be a second thing to remember to clear, and
    /// the figure would outlive the workspace on any page that publishes a title without ever
    /// thinking about usage.
    /// </summary>
    public Guid? UsageProposalId { get; private set; }

    /// <summary>
    /// Spend on <see cref="UsageProposalId"/> in USD as stored, or null until the first query lands
    /// — and never a figure belonging to another proposal, since the only setter refuses an id that
    /// is not the current one. Null renders nothing rather than 0,00: a number that is briefly
    /// wrong is worse than one that is briefly absent, when being trusted is the point of it.
    /// </summary>
    public decimal? UsageCostUsd { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Counts publications, so a page leaving can tell whether the header is still the one it put
    /// there. See <see cref="ClearIfCurrent"/> — this is what makes the handover order-independent.
    /// </summary>
    private int _publication;

    /// <returns>
    /// A stamp identifying this publication, to hand back to <see cref="ClearIfCurrent"/> on the way
    /// out. Bumped even when nothing visibly changed: it identifies the publisher, and the
    /// short-circuit below exists only to avoid a pointless re-render.
    /// </returns>
    public int Set(string? title, string? subtitle = null, string? badge = null,
        Guid? usageProposalId = null)
    {
        var publication = ++_publication;

        if (Title == title && Subtitle == subtitle && Badge == badge
            && UsageProposalId == usageProposalId) return publication;

        // The id has to be in the comparison above, and the cost has to go with it: two proposals
        // can share a title, a client and an archive state — a duplicated bid, or two with no
        // client name — and navigating between them reuses the one ProposalPage instance, so
        // Clear never runs and the second would otherwise wear the first's spend. Leaving the cost
        // alone when the id is unchanged is what keeps the figure steady across a tab switch,
        // which republishes an identical title.
        if (UsageProposalId != usageProposalId) UsageCostUsd = null;

        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        UsageProposalId = usageProposalId;
        Changed?.Invoke();
        return publication;
    }

    /// <summary>
    /// The spend figure once queried. Ignored for any proposal no longer on screen: the query is
    /// async, and a slow one landing after the user has moved on would show one bid's total under
    /// another's name.
    /// </summary>
    public void SetUsageCost(Guid proposalId, decimal costUsd)
    {
        if (UsageProposalId != proposalId || UsageCostUsd == costUsd) return;
        UsageCostUsd = costUsd;
        Changed?.Invoke();
    }

    /// <summary>Blanks the header unconditionally — for a page clearing its own title while it is
    /// still the page on screen.</summary>
    public int Clear() => Set(null);

    /// <summary>
    /// Blanks the header on the way out, but only if nothing has published since — which is what a
    /// page disposing has to do rather than calling <see cref="Clear"/>.
    /// <para>
    /// Blazor initialises the incoming page during the render batch and runs the outgoing page's
    /// <c>Dispose</c> from the batch's disposal queue afterwards, so a plain Clear in Dispose wipes
    /// the title the new page has already set: leaving a proposal for /settings used to land on a
    /// nameless bar. Nothing in the framework promises that order either way, and a page that
    /// publishes after an <c>await</c> gets the opposite one depending on whether the await actually
    /// yielded — so the fix is not to reorder the calls but to make the handover not care.
    /// </para>
    /// </summary>
    public void ClearIfCurrent(int publication)
    {
        if (publication != _publication) return;
        Clear();
    }
}
