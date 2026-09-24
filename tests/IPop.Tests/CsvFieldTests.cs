using IPop.Api.Endpoints;
using Xunit;

namespace IPop.Tests;

public class CsvFieldTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+62 812", "'+62 812")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Budi", "Budi")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData(null, "")]
    public void Escapes_formulas_and_separators(string? input, string expected)
        => Assert.Equal(expected, DirectoryEndpoints.CsvField(input));
}
