using Saga.Web;

namespace Saga.Tests;

/// <summary>
/// The one place a money figure is formatted, shared by the app bar, the Usage tab, the per-service
/// and per-meter tables and the admin roll-up. Costs are stored in USD and shown in kroner, so both
/// the conversion and the Danish separators are load-bearing: a decimal point where a comma belongs
/// reads as a thousand times the money.
/// </summary>
public class MoneyTests
{
    [Theory]
    // Danish separators: comma for the decimal, point for the thousand.
    [InlineData(0.016, 6.9, "0,11 DKK")]
    [InlineData(1000, 6.9, "6.900,00 DKK")]
    [InlineData(0, 6.9, "0,00 DKK")]
    public void An_amount_is_converted_and_shown_in_kroner(decimal usd, decimal rate, string expected)
        => Assert.Equal(expected, Money.Display(usd, rate));

    /// <summary>
    /// No rate configured is what production runs today, and it must not report kroner that were
    /// never calculated — the "$" is the whole signal that this figure was not converted.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void With_no_rate_the_stored_dollars_are_shown_as_they_are(decimal rate)
        => Assert.Equal("$0.02", Money.Display(0.016m, rate));

    /// <summary>The column headers have to name whichever currency the values under them use.</summary>
    [Fact]
    public void The_unit_label_follows_the_rate()
    {
        Assert.Equal("DKK", Money.Currency(6.9m));
        Assert.Equal("USD", Money.Currency(0m));
    }
}
