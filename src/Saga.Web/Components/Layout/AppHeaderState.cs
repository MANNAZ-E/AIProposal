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

    public event Action? Changed;

    public void Set(string? title, string? subtitle = null, string? badge = null)
    {
        if (Title == title && Subtitle == subtitle && Badge == badge) return;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        Changed?.Invoke();
    }

    public void Clear() => Set(null);
}
