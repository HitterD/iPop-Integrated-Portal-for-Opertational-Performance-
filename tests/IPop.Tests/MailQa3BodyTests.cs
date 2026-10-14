using System.Text.RegularExpressions;
using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebMail = IPop.Web.Services;

namespace IPop.Tests;

/// Mail QA 3 (2026-10-14): EPop/Outlook bodies carry big empty gaps (empty paragraphs, &lt;br&gt; runs, fixed-height
/// spacers). The reading view keeps at most one blank line; nothing with content goes; Original formatting is untouched.
public class MailQa3BodyTests
{
    private static MailDetail Detail(string sender) => new(
        Guid.NewGuid(), Guid.NewGuid(), sender, sender, "Re: CA Online", "<p>x</p>", true, null,
        DateTime.UtcNow, null, null, 0, false, true, "inbox", null, null, [], [], []);

    // ───────────── body: empty gaps ─────────────

    private static string Text(string h) => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(h, "<[^>]+>", "")), @"\s+", "");
    private static int Count(string s, string sub) => Regex.Matches(s, Regex.Escape(sub)).Count;

    [Fact]
    public void Br_runs_collapse_to_one_blank_line()
    {
        var r = MailBodyLayout.Prepare("<p>Dear all<br><br><br><br>Please fill<br>the survey<br><br>Thanks</p>");
        Assert.True(r.Changed);
        Assert.Contains("Dear all<br><br>Please fill<br>the survey<br><br>Thanks", r.Html);
    }

    [Fact]
    public void Breaks_right_after_a_block_or_at_its_start_keep_one_blank_line_only()
    {
        var r = MailBodyLayout.Prepare("<p>a</p><hr><br><br><strong>LANGKAH</strong><div><br><br>b</div>");
        Assert.Contains("<hr><br><strong>LANGKAH</strong>", r.Html);
        Assert.Contains("<div><br>b</div>", r.Html);
        Assert.Contains("<hr>", r.Html);
    }

    [Fact]
    public void Empty_paragraph_runs_keep_one_and_spacers_lose_fixed_heights()
    {
        var html = "<p>Terima kasih</p><p style=\"margin-top:0;margin-bottom:0\"><br></p><p style=\"margin-top:0;margin-bottom:0\"><span><br></span></p>"
                 + "<p>&nbsp;</p><div style=\"height:120px\"></div><p>--<br>Regards</p>";
        var r = MailBodyLayout.Prepare(html);
        Assert.Equal(1, Count(r.Html, "<p style=\"margin-top:0;margin-bottom:0\">"));
        Assert.DoesNotContain("height:120px", r.Html);
        Assert.DoesNotContain("&nbsp;", r.Html);
        Assert.Equal(Text(html), Text(r.Html));
    }

    [Fact]
    public void Br_only_inline_wrappers_and_nbsp_lines_count_as_blank_lines()
    {
        var r = MailBodyLayout.Prepare("<div>Hi<br><span><br></span><span><br></span>&nbsp;<br><span><br></span>there</div>");
        Assert.Equal(2, Count(r.Html, "<br>"));
        Assert.Contains("there", r.Html);
    }

    [Fact]
    public void Content_is_never_removed_images_hr_filled_cells_and_coloured_spacers_stay()
    {
        var html = "<p>a</p><p><br></p><p><img src=\"/api/attachments/x\" height=\"300\"></p><p><br></p><p><br></p>"
                 + "<table><tbody><tr><td height=\"40\">cell</td><td height=\"40\"></td></tr></tbody></table>"
                 + "<table height=\"80\"><tbody><tr><td></td></tr></tbody></table>"
                 + "<div style=\"height:4px;background:#c00\"></div><hr><hr><p><a name=\"top\"></a></p><p><br></p><p>end</p>";
        var r = MailBodyLayout.Prepare(html);
        Assert.Contains("<img src=\"/api/attachments/x\" height=\"300\">", r.Html);
        Assert.Contains("<td height=\"40\">cell</td>", r.Html);
        Assert.Contains("<td></td>", r.Html);                    // the empty cell stays, without its fixed height
        Assert.DoesNotContain("table height=\"80\"", r.Html);
        Assert.Contains("height:4px;background:#c00", r.Html);   // a coloured bar is a divider, not a gap
        Assert.Equal(2, Count(r.Html, "<hr>"));
        Assert.Contains("<a name=\"top\"></a>", r.Html);
        Assert.Equal(Text(html), Text(r.Html));
    }

    [Fact]
    public void Pre_blocks_and_original_formatting_are_untouched()
    {
        var pre = "<pre>a<br><br><br>b</pre>";
        Assert.Contains("a<br><br><br>b", MailBodyLayout.Prepare(pre).Html);

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<MailReadingPrefs>();
        var stored = "<p>a<br><br><br><br>b</p>";
        var cut = ctx.Render<MailBody>(p => p.Add(c => c.Mail, Detail("S1") with { Body = stored }));
        Assert.Equal(2, Count(cut.Find(".mrd-body").InnerHtml, "<br>"));
        cut.Find("button.mrd-fmt").Click();
        Assert.Equal(4, Count(cut.Find(".mrd-orig").InnerHtml, "<br>"));
    }

    [Fact]
    public void The_real_epop_survey_mail_loses_its_gaps_but_no_text_or_images()
    {
        var html = "<p><strong><span style=\"color:#0000ff;font-size:14pt\">REMINDER PENGISIAN CA &amp; EES</span></strong><br><br>"
                 + "<img src=\"/api/attachments/788c343c-ea96-4fed-b581-4e0627f75995\" width=\"692\" alt=\"\" data-ipop-att=\"788c343c-ea96-4fed-b581-4e0627f75995\"><br><br>"
                 + "Pada tahun ini.<br>Mohon bantuan.<br></p><hr><br><br><strong>LANGKAH :</strong><br><br>1. Akses<br><br><hr><br><br><strong>Catatan:</strong>";
        var r = MailBodyLayout.Prepare(html);
        Assert.Equal(Text(html), Text(r.Html));
        Assert.Equal(Count(html, "<img"), Count(r.Html, "<img"));
        Assert.Equal(2, Count(r.Html, "<hr>"));
        Assert.DoesNotContain("<hr><br><br>", r.Html);
    }


    // The shell's generic ".body { display:flex; flex-direction:column; gap:14px }" (css/shell/layout.css) made every
    // loose <br> / <strong> at the root of a mail body its own flex row 14 px apart: 2–3× the blank line.
    [Fact]
    public void Mail_body_is_normal_text_flow_not_the_shell_flex_column()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        var css = File.ReadAllText(Path.Combine(root!.FullName, "src/IPop.Web/wwwroot/css/v6/mail-receipts.css"));
        var rule = Regex.Match(css, @"\.mail-page \.mrd \.mrd-body\.body\s*\{([^}]*)\}").Groups[1].Value;
        Assert.Matches(@"display:\s*block", rule);
        Assert.Matches(@"gap:\s*0", rule);
        Assert.Matches(@"overflow:\s*visible", rule);
        Assert.Matches(@"padding:\s*0", rule);
        Assert.DoesNotContain(".mrd-orig.body {", css);   // Original formatting stays exactly as it was
    }
}
