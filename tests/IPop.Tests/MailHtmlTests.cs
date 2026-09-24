using IPop.Infrastructure.Mail;
using Xunit;

namespace IPop.Tests;

public class MailHtmlTests
{
    [Theory]
    [InlineData("<p>hi</p><script>alert(1)</script>", "<p>hi</p>")]
    [InlineData("<img src=x onerror=\"alert(1)\">", "<img src=\"x\">")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    public void StripsScriptVectors(string input, string expected) =>
        Assert.Equal(expected, MailHtml.Sanitize(input));

    [Fact]
    public void KeepsComposerFormatting()
    {
        var html = "<p><b>Bold</b> <i>it</i> <a href=\"mailto:a@b.c\">m</a> <span class=\"mention\" data-nik=\"123\">@A</span></p>";
        Assert.Equal(html, MailHtml.Sanitize(html));
    }
}
