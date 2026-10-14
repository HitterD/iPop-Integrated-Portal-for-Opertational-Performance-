using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebMail = IPop.Web.Services;

namespace IPop.Tests;

/// Reading layout for rich / EPop mail (spec 2026-10-14 §5): a render-time transform that never touches the stored
/// body. Quoted history collapses under the existing mail-quote expander; oversized inline type is normalised;
/// "Original formatting" shows the sanitized original, remembered per message for the session.
public class MailBodyLayoutTests
{
    private const string Toggle = "<summary class=\"mail-quote-toggle\"";

    [Fact]
    public void Ipop_composed_mail_is_left_alone()
    {
        var html = "<p>Halo <b>tim</b>, <a href=\"https://x.co\" target=\"_blank\">link</a></p>"
                 + "<details class=\"mail-quote\"><summary class=\"mail-quote-toggle\" title=\"Show quoted message\">⋯</summary>"
                 + "<div class=\"mail-quote-inner\"><div class=\"mail-quote-attr\">On Mon, A wrote:</div><blockquote><p>old</p></blockquote></div></details>";
        var r = MailBodyLayout.Prepare(html);
        Assert.Equal(html, r.Html);
        Assert.False(r.Changed);
        Assert.False(r.QuoteCollapsed);
        Assert.False(r.OffersOriginal);
    }

    [Fact]
    public void Epop_original_divider_inside_a_paragraph_is_split_and_collapsed()
    {
        var html = "<p>Dear Bu Alfith <br><br>minta tolong<br><br>Terimakasih,<br>Bagastyo<br><br>-----Original e/pop-----<br>From: Desy Ekarini<br>Sent: 11/14/2024 11:41:58 AM<br>Subject: ZOOM 1<br><br>Dear Bagas,</p>";
        var r = MailBodyLayout.Prepare(html);
        Assert.True(r.QuoteCollapsed);
        Assert.True(r.OffersOriginal);
        var at = r.Html.IndexOf("<details class=\"mail-quote mrd-quote\">", StringComparison.Ordinal);
        Assert.True(at > 0);
        var before = r.Html[..at];
        var after = r.Html[at..];
        Assert.Contains("Bagastyo", before);
        Assert.DoesNotContain("Original e/pop", before);
        Assert.Contains("-----Original e/pop-----", after);
        Assert.Contains("From: Desy Ekarini", after);
        Assert.Contains(Toggle, after);
        Assert.Equal(Count(r.Html, "<p>"), Count(r.Html, "</p>"));   // split paragraphs stay balanced
    }

    [Fact]
    public void Outlook_from_sent_header_and_gmail_quote_collapse()
    {
        var outlook = "<div><p>Noted, thanks.</p></div><div><p><b>From:</b> Rina<br><b>Sent:</b> Monday<br><b>Subject:</b> Budget</p><p>old body</p></div>";
        var r = MailBodyLayout.Prepare(outlook);
        Assert.True(r.QuoteCollapsed);
        Assert.Contains("Noted, thanks.", r.Html[..r.Html.IndexOf("<details", StringComparison.Ordinal)]);
        Assert.Contains("old body", r.Html[r.Html.IndexOf("<details", StringComparison.Ordinal)..]);

        var gmail = "<div>Sure</div><div class=\"gmail_quote\"><div class=\"gmail_attr\">On Mon, A wrote:</div><blockquote class=\"gmail_quote\">x</blockquote></div>";
        var g = MailBodyLayout.Prepare(gmail);
        Assert.True(g.QuoteCollapsed);
        Assert.StartsWith("<div>Sure</div><details", g.Html);
    }

    [Fact]
    public void Bare_blockquote_after_text_collapses_but_a_quote_with_nothing_before_it_does_not()
    {
        Assert.True(MailBodyLayout.Prepare("<p>ok</p><blockquote><p>older</p></blockquote>").QuoteCollapsed);
        var fwd = MailBodyLayout.Prepare("<p>-----Original e/pop-----<br>From: A<br>Sent: B</p>");
        Assert.False(fwd.QuoteCollapsed);
        Assert.DoesNotContain("<details", fwd.Html);
    }

    [Fact]
    public void Oversized_type_is_capped_small_type_raised_fonts_and_line_height_dropped_colours_kept()
    {
        var html = "<p><strong><span style=\"color:#ff00ff;font-size:20pt\">(H-1)</span></strong>"
                 + "<span style=\"font-family:&quot;Calibri&quot;, sans-serif;font-size:11pt;line-height:1.1\">body</span>"
                 + "<span style=\"font-size:7pt\">tiny</span></p>";
        var r = MailBodyLayout.Prepare(html);
        Assert.True(r.Changed);
        Assert.True(r.OffersOriginal);
        Assert.Contains("color:#ff00ff", r.Html);
        Assert.Contains("font-size:17px", r.Html);
        Assert.DoesNotContain("20pt", r.Html);
        Assert.DoesNotContain("Calibri", r.Html);
        Assert.DoesNotContain("line-height", r.Html);
        Assert.DoesNotContain("11pt", r.Html);
        Assert.Contains("font-size:13px", r.Html);
        Assert.Contains("<strong>", r.Html);
    }

    [Fact]
    public void Images_keep_their_inline_attachment_ids_and_tables_keep_cells()
    {
        var html = "<p><img src=\"/api/attachments/a0c0f5bf-8dd6-490b-9067-7575e9c4ff8f\" width=\"1526\" alt=\"\" data-ipop-att=\"a0c0f5bf-8dd6-490b-9067-7575e9c4ff8f\"></p>"
                 + "<table style=\"width:1200px\"><tbody><tr><td>a &amp; b</td></tr></tbody></table>";
        var r = MailBodyLayout.Prepare(html);
        Assert.Contains("data-ipop-att=\"a0c0f5bf-8dd6-490b-9067-7575e9c4ff8f\"", r.Html);
        Assert.Contains("a &amp; b", r.Html);
        Assert.DoesNotContain("1200px", r.Html);
        Assert.True(r.OffersOriginal);
    }

    [Fact]
    public void Split_inside_a_table_is_not_attempted()
    {
        var html = "<table><tbody><tr><td>Hi<br>-----Original Message-----<br>From: A<br>Sent: B</td></tr></tbody></table>";
        var r = MailBodyLayout.Prepare(html);
        Assert.False(r.QuoteCollapsed);
        Assert.Equal(html, r.Html);
    }

    [Fact]
    public void Plain_text_or_empty_is_passed_through()
    {
        Assert.Equal("", MailBodyLayout.Prepare(null).Html);
        Assert.Equal("just text", MailBodyLayout.Prepare("just text").Html);
    }

    [Fact]
    public void Malformed_input_never_throws_and_keeps_the_text()
    {
        var r = MailBodyLayout.Prepare("<p>open <b>bold</p><div>x</span> y");
        Assert.Contains("bold", r.Html);
        Assert.Contains("y", r.Html);
    }

    // ── component ──

    private static MailDetail Detail(string body) => new(
        Guid.NewGuid(), Guid.NewGuid(), "S", "S1", "Sub", body, true, null, DateTime.UtcNow, null, null, 0, false, true, "inbox", null, null, [], [], []);

    [Fact]
    public void Body_component_renders_the_reading_column_and_toggles_original_formatting_per_message()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<MailReadingPrefs>();
        var stored = "<p>Hi<br>Bagas<br>-----Original e/pop-----<br>From: A<br>Sent: B</p><p><span style=\"font-size:26pt\">BIG</span></p>";
        var d = Detail(stored);
        var cut = ctx.Render<MailBody>(p => p.Add(c => c.Mail, d));
        Assert.NotNull(cut.Find(".mrd-body"));
        Assert.Contains("mrd-quote", cut.Markup);
        var sw = cut.Find("button.mrd-fmt");
        Assert.Equal("false", sw.GetAttribute("aria-checked"));

        sw.Click();
        Assert.Equal("true", cut.Find("button.mrd-fmt").GetAttribute("aria-checked"));
        Assert.Empty(cut.FindAll(".mrd-body"));
        Assert.Contains("26pt", cut.Find(".mrd-orig").InnerHtml);         // the sanitized original, untouched
        Assert.Contains("Original formatting", cut.Find(".mrd-orig-note").TextContent);

        // Remembered for the session: a fresh render of the same message opens in original mode; another does not.
        var again = ctx.Render<MailBody>(p => p.Add(c => c.Mail, d));
        Assert.NotNull(again.Find(".mrd-orig"));
        var other = ctx.Render<MailBody>(p => p.Add(c => c.Mail, Detail(stored)));
        Assert.Empty(other.FindAll(".mrd-orig"));
    }

    [Fact]
    public void Simple_mail_gets_the_column_but_no_toggle_and_epop_mail_gets_the_archive_note()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<MailReadingPrefs>();
        var cut = ctx.Render<MailBody>(p => p.Add(c => c.Mail, Detail("<p>hi</p>")));
        Assert.NotNull(cut.Find(".mrd-body"));
        Assert.Empty(cut.FindAll(".mrd-fmt"));

        var at = new DateTime(2026, 10, 12, 9, 52, 0, DateTimeKind.Utc);
        var epop = ctx.Render<MailBody>(p => p.Add(c => c.Mail, Detail("<p>hi</p>") with { SentAt = at }).Add(c => c.FromEpop, true));
        Assert.Contains($"Imported from EPop · {at.ToLocalTime():d MMM yyyy}", epop.Find(".mrd-arch").TextContent);
    }

    [Fact]
    public void Reading_pane_and_thread_render_bodies_through_the_layout_component()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        var page = File.ReadAllText(Path.Combine(root!.FullName, "src/IPop.Web/Pages/Mail.razor"));
        var stack = File.ReadAllText(Path.Combine(root.FullName, "src/IPop.Web/Components/Mail/MailThreadStack.razor"));
        Assert.Contains("<MailBody ", page);
        Assert.Contains("<MailBody ", stack);
        Assert.DoesNotContain("@((MarkupString)detail.Body)", page);
        Assert.DoesNotContain("@((MarkupString)body.Body)", stack);
        Assert.Contains("data-mail-seen", stack);   // the receipt tracker still watches the shown body
    }

    private static int Count(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
