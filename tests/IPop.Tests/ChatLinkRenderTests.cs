using IPop.Application.Chat.Mentions;
using IPop.Application.Links;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Frontend link rendering: MentionRenderer linkify, markdown shielding, FirstWebLink and the
/// preview cache. Only http(s)/mailto hrefs may ever be emitted.
public class ChatLinkRenderTests
{
    private static readonly Guid AnnId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Html(string content) =>
        MentionRenderer.ToHtml(content, new Dir(new() { [AnnId] = ("N1", "Ann Lee") }), isGroupRoom: true);

    [Fact]
    public void Web_link_becomes_safe_anchor()
    {
        var html = Html("see https://example.com/a?b=1&c=2 now");
        Assert.Contains("<a class=\"msg-link\" href=\"https://example.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer\"", html);
        Assert.StartsWith("see ", html);
        Assert.EndsWith("</a> now", html);
    }

    [Fact]
    public void Www_link_gets_https_href()
    {
        var html = Html("go to www.santos.co.id");
        Assert.Contains("href=\"https://www.santos.co.id\"", html);
        Assert.Contains(">www.santos.co.id</a>", html);
    }

    [Fact]
    public void Xss_in_url_is_escaped_and_never_breaks_out()
    {
        var html = Html("https://x.com/\"><script>alert(1)</script>");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"><", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(document.cookie)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void Dangerous_schemes_are_never_linked(string text)
    {
        var html = Html("click " + text);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"vbscript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"file:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Null(MentionRenderer.FirstWebLink("click " + text));
    }

    [Fact]
    public void Mention_token_and_link_render_together()
    {
        var html = Html($"@[Ann Lee](u:{AnnId:D}) look https://example.com/x");
        Assert.Contains("class=\"mention user-mention\" data-nik=\"N1\"", html);
        Assert.Contains("href=\"https://example.com/x\"", html);
    }

    [Fact]
    public void Legacy_mention_still_highlighted_beside_a_link_but_not_inside_it()
    {
        var html = Html("@budi check https://example.com/@team/page");
        Assert.Contains("<span class=\"mention legacy\">@budi</span>", html);
        Assert.Contains("href=\"https://example.com/@team/page\"", html);
        Assert.DoesNotContain("<span class=\"mention legacy\">@team", html);
    }

    [Fact]
    public void Email_becomes_mailto_with_data_attribute()
    {
        var html = Html("mail me at ann.lee@santos.co.id please");
        Assert.Contains("href=\"mailto:ann.lee@santos.co.id\"", html);
        Assert.Contains("data-mailto=\"ann.lee@santos.co.id\"", html);
        Assert.DoesNotContain("mention legacy", html);
    }

    [Fact]
    public void Unc_path_with_spaces_uses_data_unc_and_no_file_href()
    {
        var html = Html(@"file is at \\192.168.2.16\sja\Folder A\Report 2026.xlsx");
        Assert.Contains("class=\"msg-link unc-link\" href=\"#\"", html);
        Assert.Contains(@"data-unc=""\\192.168.2.16\sja\Folder A\Report 2026.xlsx""", html);
        Assert.DoesNotContain("file:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_http_and_mailto_hrefs_are_emitted()
    {
        var html = Html(@"a https://a.com b www.b.com c x@y.com d \\srv\share\x javascript:alert(1)");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "href=\"([^\"]*)\""))
        {
            var href = m.Groups[1].Value;
            Assert.True(href == "#" || href.StartsWith("https://") || href.StartsWith("http://") || href.StartsWith("mailto:"), href);
        }
    }

    [Fact]
    public void Markdown_does_not_touch_link_internals()
    {
        var html = ChatFormatters.FormatMarkdown(Html("see https://example.com/a_b_c/*x*/ and _this_"));
        Assert.Contains("href=\"https://example.com/a_b_c/*x*/\"", html);
        Assert.Contains(">https://example.com/a_b_c/*x*/</a>", html);
        Assert.Contains("<em>this</em>", html);
    }

    [Fact]
    public void FirstWebLink_skips_email_and_unc_and_mention_tokens()
    {
        var content = $@"@[Ann Lee](u:{AnnId:D}) x@y.com \\srv\share
then https://first.example/p and https://second.example";
        Assert.Equal("https://first.example/p", MentionRenderer.FirstWebLink(content));
        Assert.Null(MentionRenderer.FirstWebLink("no links here"));
        Assert.Null(MentionRenderer.FirstWebLink(null));
    }

    [Fact]
    public async Task Preview_service_dedupes_in_flight_and_caches_null()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        var svc = new LinkPreviewService(async (url, ct) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return url.Contains("none") ? null : new LinkPreviewDto(url, "external", "T", null, null, null, null);
        });
        var a = svc.GetAsync("https://a.example");
        var b = svc.GetAsync("https://a.example");
        Assert.Same(a, b);
        var n = svc.GetAsync("https://none.example");
        gate.SetResult();
        Assert.Equal("T", (await a)!.Title);
        Assert.Null(await n);
        Assert.True(svc.TryGetCached("https://none.example", out var cachedNone));
        Assert.Null(cachedNone);
        _ = await svc.GetAsync("https://a.example");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Preview_service_limits_concurrency_and_swallows_errors()
    {
        var running = 0;
        var peak = 0;
        var svc = new LinkPreviewService(async (url, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) peak = Math.Max(peak, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            if (url.EndsWith("/boom")) throw new HttpRequestException("x");
            return new LinkPreviewDto(url, "external", "T", null, null, null, null);
        });
        var tasks = Enumerable.Range(0, 10).Select(i => svc.GetAsync($"https://h{i}.example/p")).ToList();
        tasks.Add(svc.GetAsync("https://h.example/boom"));
        var results = await Task.WhenAll(tasks);
        Assert.True(peak <= LinkPreviewService.MaxConcurrent, $"peak {peak}");
        Assert.Null(results[^1]);
        Assert.All(results[..^1], r => Assert.NotNull(r));
    }

    private sealed class Dir(Dictionary<Guid, (string Nik, string Name)> map) : IMentionDirectory
    {
        public bool TryResolve(Guid id, out string nik, out string name, out bool active)
        {
            if (map.TryGetValue(id, out var v)) { nik = v.Nik; name = v.Name; active = true; return true; }
            nik = name = ""; active = false; return false;
        }
    }
}
