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
    public void KeepsParagraphSpacing()
    {
        // The editor pins paragraph margins inline so other mail clients show the same spacing.
        var html = "<p style=\"margin-top: 0; margin-bottom: 6pt\">a</p>";
        Assert.Equal(html, MailHtml.Sanitize(html));
    }

    [Fact]
    public void KeepsComposerFormatting()
    {
        var html = "<p><b>Bold</b> <i>it</i> <a href=\"mailto:a@b.c\">m</a> <span class=\"mention\" data-nik=\"123\">@A</span></p>";
        Assert.Equal(html, MailHtml.Sanitize(html));
    }

    private static string Png(int bytes) =>
        "data:image/png;base64," + Convert.ToBase64String(new byte[bytes]);

    [Fact]
    public void Keeps_pasted_raster_images_including_large_ones()
    {
        foreach (var src in new[] { Png(64), Png(900_000) })
        {
            var html = $"<img src=\"{src}\">";
            Assert.Equal(html, MailHtml.Sanitize(html));
        }
    }

    [Theory]
    [InlineData("<img src=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=\">", "<img>")]
    [InlineData("<img src=\"data:text/html;base64,PHNjcmlwdD4=\">", "<img>")]
    [InlineData("<a href=\"data:image/png;base64,AAAA\">x</a>", "<a>x</a>")]
    public void Blocks_data_urls_outside_raster_img(string input, string expected) =>
        Assert.Equal(expected, MailHtml.Sanitize(input));

    [Fact]
    public void Drops_images_over_two_megabytes()
    {
        var html = $"<img src=\"{Png(2 * 1024 * 1024 + 4096)}\">";
        Assert.Equal("<img>", MailHtml.Sanitize(html));
    }

    [Fact]
    public void Keeps_office_formatting_from_the_editor()
    {
        var html = "<p style=\"text-align: center; line-height: 1.5; margin-left: 40px\">"
            + "<span style=\"font-family: Calibri; font-size: 14pt; color: rgb(255, 0, 0); background-color: yellow\">x</span>"
            + "H<sub>2</sub>O x<sup>2</sup> <mark>hi</mark></p><hr>"
            + "<table style=\"border-collapse: collapse; width: 100%\"><tbody><tr>"
            + "<td colspan=\"2\" rowspan=\"1\" style=\"border: 1px solid rgb(0, 0, 0); vertical-align: top\">c</td>"
            + "</tr></tbody></table>";
        var clean = MailHtml.Sanitize(html);
        // Colours are re-serialised (rgb → rgba) by the CSS parser; the properties must survive.
        foreach (var part in new[] { "text-align: center", "line-height: 1.5", "margin-left: 40px",
                     "font-family: Calibri", "font-size: 14pt", "color: rgba(255, 0, 0, 1)", "background-color:",
                     "<sub>2</sub>", "<sup>2</sup>", "<mark>hi</mark>", "<hr>", "border-collapse: collapse",
                     "width: 100%", "colspan=\"2\"", "rowspan=\"1\"", "border:", "vertical-align: top" })
            Assert.Contains(part, clean);
    }

    [Fact]
    public void Word_paste_keeps_text_but_loses_office_xml_and_handlers()
    {
        var html = "<p class=\"MsoNormal\" onclick=\"x()\"><o:p></o:p><b>Hi</b><script>x()</script></p>";
        var clean = MailHtml.Sanitize(html);
        Assert.Contains("<b>Hi</b>", clean);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("script", clean);
        Assert.DoesNotContain("o:p", clean);
    }
}
