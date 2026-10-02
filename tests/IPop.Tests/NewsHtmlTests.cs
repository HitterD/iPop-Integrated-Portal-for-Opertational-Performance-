using IPop.Infrastructure.News;
using Xunit;

namespace IPop.Tests;

/// The News sanitizer is deliberately stricter than MailHtml: no scripts, no event handlers, no
/// iframes from the caller, no data: URIs (Mail allows inline base64 images), no SVG, and no stored
/// URLs for internal media — only canonical ids the delivery layer resolves.
public class NewsHtmlTests
{
    // ── Scripting ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<p>ok</p><script>alert(1)</script>")]
    [InlineData("<p onclick=\"alert(1)\">ok</p>")]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=\"https://evil.example\"></object>")]
    [InlineData("<p style=\"background-image:url(https://evil.example/x.png)\">ok</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"a\"></form>")]
    public void Scripting_and_remote_resource_markup_is_stripped(string attack)
    {
        var clean = NewsHtml.Sanitize(attack);
        Assert.DoesNotContain("alert(1)", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inline_base64_images_are_not_allowed_in_news()
    {
        var clean = NewsHtml.Sanitize("<img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==\">");
        Assert.DoesNotContain("base64", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_formatting_survives()
    {
        var clean = NewsHtml.Sanitize("<p>Hello <b>bold</b> <i>it</i> <u>u</u></p><ul><li>one</li></ul><h2>H</h2><hr>");
        Assert.Contains("<b>bold</b>", clean);
        Assert.Contains("<li>one</li>", clean);
        Assert.Contains("<h2>H</h2>", clean);
    }

    [Fact]
    public void Inline_alignment_and_word_formatting_survive()
    {
        var clean = NewsHtml.Sanitize(
            """<p style="text-align: center; font-family: Calibri; font-size: 14pt; line-height: 1.5">centered</p>""");
        // The sanitizer re-formats declarations (AngleSharp normalizes colours to rgba and adds a
        // space after the colon), so assert on property presence, not on an exact value string.
        Assert.Matches(@"text-align\s*:\s*center", clean);
        Assert.Matches(@"font-family\s*:", clean);
        Assert.Matches(@"font-size\s*:", clean);
        Assert.Matches(@"line-height\s*:", clean);
        Assert.Contains("centered", clean);
    }

    [Fact]
    public void Unsafe_css_inside_style_is_dropped()
    {
        // Allowed properties are kept; anything that could reach the network or execute is not.
        var clean = NewsHtml.Sanitize(
            """<p style="text-align: center; background-image: url(https://evil.example/x.png); color: red">x</p>""");
        // Property presence only: the library rewrites `color: red` to `rgba(255, 0, 0, 1)`.
        Assert.Matches(@"text-align\s*:\s*center", clean);
        Assert.Matches(@"color\s*:", clean);
        Assert.DoesNotContain("background-image", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disallowed_css_properties_are_dropped_even_when_well_formed()
    {
        // position is not on the allow-list, so it must not survive alongside an allowed property.
        var clean = NewsHtml.Sanitize("""<p style="position: fixed; text-align: right">x</p>""");
        Assert.Matches(@"text-align\s*:\s*right", clean);
        Assert.DoesNotContain("position", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_https_links_survive()
    {
        var clean = NewsHtml.Sanitize("<a href=\"https://intranet.example/page\">link</a>");
        Assert.Contains("https://intranet.example/page", clean);
    }

    // ── Canonical internal media attributes ──────────────────────────────

    [Fact]
    public void Canonical_media_id_is_kept_and_src_is_never_stored()
    {
        var id = Guid.NewGuid();
        var clean = NewsHtml.Sanitize($"<img src=\"https://api.example/news/media/{id}?ticket=abc\" data-news-media=\"{id}\">");

        Assert.Contains($"data-news-media=\"{id}\"", clean);
        Assert.DoesNotContain("ticket=abc", clean);   // an expiring ticket must never be persisted
        Assert.DoesNotContain("api.example", clean);
        Assert.DoesNotContain("src=", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_media_id_is_dropped_entirely()
    {
        var clean = NewsHtml.Sanitize("<img data-news-media=\"not-a-guid\">");
        Assert.DoesNotContain("data-news-media", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Media_placeholder_on_figure_is_kept()
    {
        var id = Guid.NewGuid();
        var clean = NewsHtml.Sanitize($"<figure data-news-media=\"{id}\"><figcaption>cap</figcaption></figure>");
        Assert.Contains($"data-news-media=\"{id}\"", clean);
        Assert.Contains("cap", clean);
    }

    // ── External media: strictly parsed, never caller HTML ───────────────

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", true)]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://www.youtube.com/watch?v=short", false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ<script>", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void Youtube_ids_are_parsed_strictly(string input, bool expected)
    {
        var id = NewsHtml.ParseYoutubeId(input);
        Assert.Equal(expected, id is not null);
        if (expected) Assert.Equal("dQw4w9WgXcQ", id);
    }

    [Fact]
    public void Valid_youtube_attribute_is_kept_and_invalid_is_dropped()
    {
        var clean = NewsHtml.Sanitize("<div data-news-youtube=\"dQw4w9WgXcQ\"></div>");
        Assert.Contains("data-news-youtube=\"dQw4w9WgXcQ\"", clean);

        var bad = NewsHtml.Sanitize("<div data-news-youtube=\"<script>alert(1)</script>\"></div>");
        Assert.DoesNotContain("data-news-youtube", bad, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trusted_embed_markup_uses_the_nocookie_host()
    {
        var html = NewsHtml.YoutubeEmbed("dQw4w9WgXcQ");
        Assert.Contains("youtube-nocookie.com/embed/dQw4w9WgXcQ", html);
        Assert.Contains("data-news-youtube=\"dQw4w9WgXcQ\"", html);
    }

    [Fact]
    public void Trusted_embed_helper_refuses_malformed_input()
    {
        Assert.Equal("", NewsHtml.YoutubeEmbed("<script>alert(1)</script>"));
        Assert.Equal("", NewsHtml.YoutubeEmbed("short"));
        Assert.Equal("", NewsHtml.YoutubeEmbed(null));
        Assert.Equal("", NewsHtml.YoutubeEmbed("https://evil.example/embed/dQw4w9WgXcQ"));
    }

    [Theory]
    [InlineData("https://cdn.example/clip.mp4", true)]
    [InlineData("https://cdn.example/clip.webm", true)]
    [InlineData("https://cdn.example:8443/a/b/clip.mp4", true)]
    [InlineData("https://cdn.example/clip.mp4?sig=abc&exp=123", true)] // signed/CDN link
    [InlineData("http://cdn.example/clip.mp4", false)]                 // https only
    [InlineData("https://cdn.example/clip.m4v", false)]                // not an approved container
    [InlineData("https://cdn.example/clip.avi", false)]
    [InlineData("https://user:pw@cdn.example/clip.mp4", false)]        // no credentials
    [InlineData("https://cdn.example/clip.mp4#frag", false)]           // no fragment
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:video/mp4;base64,AAAA", false)]
    public void Direct_video_urls_are_narrowly_accepted(string url, bool expected)
    {
        var clean = NewsHtml.Sanitize($"<div data-news-video-url=\"{url}\"></div>");
        Assert.Equal(expected, clean.Contains("data-news-video-url", StringComparison.OrdinalIgnoreCase));
    }

    // ── Plain-text derivation ────────────────────────────────────────────

    [Fact]
    public void ToPlainText_never_returns_markup()
    {
        var text = NewsHtml.ToPlainText("<p>Hello <b>world</b></p><img data-news-media=\"x\">", "html");
        Assert.Equal("Hello world", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void ToPlainText_keeps_plain_bodies_verbatim()
    {
        Assert.Equal("a < b & c", NewsHtml.ToPlainText("a < b & c", "plain"));
    }

    // ── Render: legacy plain rows keep their old output ──────────────────

    [Fact]
    public void Render_escapes_plain_bodies()
    {
        Assert.Equal("&lt;b&gt;x&lt;/b&gt;", NewsHtml.Render("<b>x</b>", "plain"));
        Assert.Equal("&lt;b&gt;x&lt;/b&gt;", NewsHtml.Render("<b>x</b>", null));   // legacy row: BodyFormat unset
    }

    [Fact]
    public void Render_sanitizes_html_bodies()
    {
        Assert.Equal("<b>x</b>", NewsHtml.Render("<b>x</b><script>alert(1)</script>", "html"));
    }

    // ── Reference extraction (for reference-aware cleanup) ───────────────

    [Fact]
    public void Referenced_media_ids_are_extracted()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var body = $"<img data-news-media=\"{a}\"><div data-news-media='{b}'></div><img data-news-media=\"nope\">";
        var ids = NewsHtml.ReferencedMediaIds(body);
        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
        Assert.Empty(NewsHtml.ReferencedMediaIds("<p>no media</p>"));
        Assert.False(NewsHtml.ReferencesMedia("<p>no media</p>"));
    }
}
