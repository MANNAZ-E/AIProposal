namespace Saga.Web;

/// <summary>
/// Drops the organisation suffix Entra carries on Mannaz display names ("Emil Lindeløv
/// Vestergaard - Mannaz"), which says nothing in an app where everyone is from Mannaz.
/// Trimmed at the point of display rather than on the stored DisplayName: the suffix is what the
/// directory actually holds, and a name that no longer matches Entra is worse than a long one.
/// </summary>
public static class PersonName
{
    private const string OrgSuffix = " - Mannaz";

    public static string Display(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var trimmed = name.TrimEnd();
        if (!trimmed.EndsWith(OrgSuffix, StringComparison.OrdinalIgnoreCase)) return trimmed;

        // A name that is nothing but the suffix keeps it — better a stale label than a blank one.
        var stripped = trimmed[..^OrgSuffix.Length].TrimEnd();
        return stripped.Length > 0 ? stripped : trimmed;
    }
}
