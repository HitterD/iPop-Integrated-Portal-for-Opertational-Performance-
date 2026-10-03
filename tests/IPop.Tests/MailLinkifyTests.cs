using IPop.Infrastructure.Mail;
using Xunit;

namespace IPop.Tests;

public class MailLinkifyTests
{
    [Fact]
    public void Plain_urls_become_external_anchors()
    {
        var html = MailHtml.ForDisplay("<p>Lihat https://example.com/a?b=1&amp;c=2. Terima kasih</p>");
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer\">https://example.com/a?b=1&amp;c=2</a>. Terima kasih", html);
    }

    [Fact]
    public void Www_and_email()
    {
        var html = MailHtml.ForDisplay("<p>www.santos.co.id atau it@santos.co.id</p>");
        Assert.Contains("<a href=\"https://www.santos.co.id\" target=\"_blank\" rel=\"noopener noreferrer\">www.santos.co.id</a>", html);
        Assert.Contains("<a href=\"mailto:it@santos.co.id\">it@santos.co.id</a>", html);
    }

    [Fact]
    public void Unc_paths_with_spaces_get_data_unc()
    {
        var html = MailHtml.ForDisplay("<p>Folder: \\\\192.168.2.16\\sja\\SJA SPJ\\LOGISTIC\\TO\\Sharing PO Kawaz</p><p>next</p>");
        Assert.Contains("<a class=\"unc-link\" href=\"#\" data-unc=\"\\\\192.168.2.16\\sja\\SJA SPJ\\LOGISTIC\\TO\\Sharing PO Kawaz\">\\\\192.168.2.16\\sja\\SJA SPJ\\LOGISTIC\\TO\\Sharing PO Kawaz</a></p>", html);
        Assert.Contains("<p>next</p>", html);
    }

    [Fact]
    public void Unc_stops_at_line_break_element()
    {
        var html = MailHtml.ForDisplay("<p>\\\\nas\\share\\a b<br>Salam</p>");
        Assert.Contains("data-unc=\"\\\\nas\\share\\a b\"", html);
        Assert.Contains("<br>Salam", html);
    }

    [Fact]
    public void Existing_anchors_and_code_are_untouched_and_no_double_wrapping()
    {
        const string input = "<p><a href=\"https://example.com\">https://example.com</a> <code>https://x.example</code></p><pre>www.y.example</pre>";
        var once = MailHtml.ForDisplay(input);
        Assert.Equal(1, Count(once, "<a "));
        Assert.Contains("<code>https://x.example</code>", once);
        Assert.Contains("<pre>www.y.example</pre>", once);
        Assert.Equal(once, MailHtml.ForDisplay(once));

        var linked = MailHtml.ForDisplay("<p>https://example.com</p>");
        Assert.Equal(linked, MailHtml.ForDisplay(linked));
        Assert.Equal(1, Count(linked, "<a "));
    }

    [Fact]
    public void Mention_chips_are_not_linkified()
    {
        var html = MailHtml.ForDisplay("<p><span class=\"mention\" data-nik=\"123\">@budi@santos.co.id</span></p>");
        Assert.DoesNotContain("<a ", html);
    }

    [Theory]
    [InlineData("<p><a href=\"javascript:alert(1)\">x</a> https://ok.example</p>")]
    [InlineData("<p>javascript:alert(1) https://ok.example</p>")]
    [InlineData("<p><img src=x onerror=alert(1)> https://ok.example</p>")]
    public void Sanitizer_still_applies(string input)
    {
        var html = MailHtml.ForDisplay(input);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://ok.example\"", html);
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_escaped()
    {
        var html = MailHtml.ForDisplay("<p>https://example.com/&lt;script&gt;alert(1)&lt;/script&gt;</p>");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_alone_does_not_linkify_drafts()
    {
        Assert.Equal("<p>https://example.com</p>", MailHtml.Sanitize("<p>https://example.com</p>"));
    }

    private static int Count(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
