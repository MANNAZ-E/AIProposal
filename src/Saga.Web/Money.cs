using System.Globalization;

namespace Saga.Web;

/// <summary>
/// Formats the USD costs stored on usage rows for display. Azure publishes list prices in USD,
/// so that is what is recorded; Mannaz reports in DKK, so the conversion happens here at render
/// time using the configured rate.
/// </summary>
public static class Money
{
    private static readonly CultureInfo Danish = CultureInfo.GetCultureInfo("da-DK");

    /// <summary>
    /// Converts a stored USD amount for display. With no rate configured the USD figure is shown
    /// as-is rather than silently reporting kroner that were never calculated.
    /// </summary>
    public static string Display(decimal usd, decimal usdToDkk)
        => usdToDkk <= 0m
            ? $"${usd.ToString("N2", CultureInfo.InvariantCulture)}"
            : (usd * usdToDkk).ToString("N2", Danish) + " kr.";

    /// <summary>The unit label for a column header, matching whichever currency Display uses.</summary>
    public static string Currency(decimal usdToDkk) => usdToDkk <= 0m ? "USD" : "DKK";
}
