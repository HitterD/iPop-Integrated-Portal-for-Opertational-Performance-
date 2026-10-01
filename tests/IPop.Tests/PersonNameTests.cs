using Xunit;
using IPop.Application;

namespace IPop.Tests;

public class PersonNameTests
{
    [Theory]
    [InlineData("GO SONNY GOZALI", "Go Sonny Gozali")]
    [InlineData("M. KHUBAN MUSAFA", "M. Khuban Musafa")]
    [InlineData("ACHMAD HALIM KD", "Achmad Halim KD")]
    [InlineData("SITI-AISYAH  PUTRI", "Siti-Aisyah Putri")]
    [InlineData("Bagas Arif", "Bagas Arif")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Display_title_cases_capitals_only(string? raw, string expected) =>
        Assert.Equal(expected, PersonName.Display(raw));

    [Theory]
    [InlineData("GO SONNY GOZALI", "Go Sonny")]
    [InlineData("M. KHUBAN MUSAFA", "Khuban Musafa")]
    [InlineData("MOH. ADI", "Adi")]
    [InlineData("R. AKHMAD KUSUMO YUDO", "Akhmad Kusumo")]
    [InlineData("MUHAMMAD AHZA NUUR WAHYU", "Muhammad Ahza")]
    [InlineData("Beny", "Beny")]
    [InlineData("M.", "M.")]
    [InlineData(null, "")]
    public void Short_is_first_two_words_after_prefix(string? raw, string expected) =>
        Assert.Equal(expected, PersonName.Short(raw));
}
