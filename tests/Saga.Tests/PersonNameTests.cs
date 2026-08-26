using Saga.Core.Domain;

namespace Saga.Tests;

public class PersonNameTests
{
    [Fact]
    public void The_org_suffix_Entra_appends_is_dropped()
        => Assert.Equal("Emil Lindeløv Vestergaard",
            PersonName.Normalise("Emil Lindeløv Vestergaard - Mannaz"));

    [Fact]
    public void A_name_without_the_suffix_is_left_alone()
        => Assert.Equal("Emil Lindeløv Vestergaard",
            PersonName.Normalise("Emil Lindeløv Vestergaard"));

    [Fact]
    public void Trailing_whitespace_does_not_hide_the_suffix()
        => Assert.Equal("Stefanie Baptiste", PersonName.Normalise("Stefanie Baptiste - MANNAZ  "));

    [Fact]
    public void A_name_that_is_only_the_suffix_keeps_it()
        => Assert.Equal("- Mannaz", PersonName.Normalise("- Mannaz"));

    [Fact]
    public void Mannaz_inside_a_name_is_not_a_suffix()
        => Assert.Equal("Mannaz Nielsen", PersonName.Normalise("Mannaz Nielsen"));

    [Fact]
    public void A_blank_name_comes_back_empty_so_the_caller_can_keep_what_it_had()
        => Assert.Equal("", PersonName.Normalise("   "));
}
