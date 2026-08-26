namespace Saga.Core.Domain;

/// <summary>
/// Drops the organisation suffix Entra carries on Mannaz display names ("Emil Lindeløv
/// Vestergaard - Mannaz"), which says nothing in an app where everyone is from Mannaz.
/// Applied where a name enters from the directory, so the suffix is never stored and every
/// screen reading <see cref="User.DisplayName"/> gets the short form without knowing why.
/// A row that already holds the suffix is corrected on its owner's next sign-in.
/// </summary>
public static class PersonName
{
    private const string OrgSuffix = " - Mannaz";

    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var trimmed = name.TrimEnd();
        if (!trimmed.EndsWith(OrgSuffix, StringComparison.OrdinalIgnoreCase)) return trimmed;

        // A name that is nothing but the suffix keeps it — better a stale label than a blank one.
        var stripped = trimmed[..^OrgSuffix.Length].TrimEnd();
        return stripped.Length > 0 ? stripped : trimmed;
    }
}
