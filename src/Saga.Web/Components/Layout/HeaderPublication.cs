namespace Saga.Web.Components.Layout;

/// <summary>
/// Ties one page's <see cref="AppHeaderState"/> publication to that page's lifetime: the token
/// <see cref="AppHeaderState.Set"/> returns, and the <see cref="AppHeaderState.ClearIfCurrent"/> call
/// it must hand back on Dispose, live here instead of being copied into every workspace-level page.
/// </summary>
public sealed class HeaderPublication(AppHeaderState header) : IDisposable
{
    private int _publication;

    public void Set(string? title, string? subtitle = null, string? badge = null, Guid? usageProposalId = null)
        => _publication = header.Set(title, subtitle, badge, usageProposalId);

    public void Clear() => _publication = header.Clear();

    public void Dispose() => header.ClearIfCurrent(_publication);
}
