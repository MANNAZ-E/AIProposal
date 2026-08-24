namespace Saga.Web;

/// <summary>
/// Short "how long ago" labels for the chat list, where a full timestamp on every row would
/// crowd out the title. Everything else in the app shows absolute dates, and should keep to it.
/// </summary>
public static class RelativeTime
{
    /// <param name="now">
    /// Passed in rather than read from the clock so this stays a pure function — the app has no
    /// clock abstraction and does not need one for a list label.
    /// </param>
    public static string Display(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalMinutes < 1) return "now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d";
        return when.ToLocalTime().ToString("dd MMM");
    }
}
