using IPop.Application.Links;
using Xunit;

namespace IPop.Tests;

public class LinkDetectorTests
{
    private static LinkSpan Single(string text)
    {
        var spans = LinkDetector.Find(text);
        return Assert.Single(spans);
    }

    [Theory]
    [InlineData("see https://example.com/a.", "https://example.com/a")]
    [InlineData("see https://example.com/a, then", "https://example.com/a")]
    [InlineData("really? https://example.com/a?!", "https://example.com/a")]
    [InlineData("ok: https://example.com/a;", "https://example.com/a")]
    [InlineData("\"https://example.com/q?x=1&y=2\"", "https://example.com/q?x=1&y=2")]
    [InlineData("http://example.com:8080/p", "http://example.com:8080/p")]
    public void Web_urls_drop_trailing_punctuation(string text, string href) =>
        Assert.Equal(href, Single(text).Href);

    [Fact]
    public void Balanced_parens_are_kept_unbalanced_dropped()
    {
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", Single("see https://en.wikipedia.org/wiki/Foo_(bar)").Href);
        Assert.Equal("https://example.com/x", Single("(see https://example.com/x)").Href);
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", Single("(https://en.wikipedia.org/wiki/Foo_(bar)).").Href);
    }

    [Fact]
    public void Www_gets_https_and_display_stays_as_typed()
    {
        var s = Single("buka www.santos.co.id ya");
        Assert.Equal(LinkKind.Web, s.Kind);
        Assert.Equal("https://www.santos.co.id", s.Href);
        Assert.Equal("www.santos.co.id", s.Display);
        Assert.Equal(5, s.Start);
        Assert.Equal("www.santos.co.id".Length, s.Length);
    }

    [Fact]
    public void Emails_become_mailto_but_not_inside_urls()
    {
        var e = Single("kirim ke budi.s@santos.co.id.");
        Assert.Equal(LinkKind.Email, e.Kind);
        Assert.Equal("mailto:budi.s@santos.co.id", e.Href);

        var u = Single("https://example.com/u/me@host.com/x");
        Assert.Equal(LinkKind.Web, u.Kind);
    }

    [Fact]
    public void Unc_path_with_spaces_runs_to_end_of_line()
    {
        var text = "Tolong cek di \\\\192.168.2.16\\sja\\SJA SPJ\\LOGISTIC\\TO\\Sharing PO Kawaz\nTerima kasih.";
        var s = Single(text);
        Assert.Equal(LinkKind.Unc, s.Kind);
        Assert.Equal("\\\\192.168.2.16\\sja\\SJA SPJ\\LOGISTIC\\TO\\Sharing PO Kawaz", s.Href);
        Assert.Equal(s.Href, s.Display);
        Assert.Equal(text.IndexOf("\\\\", StringComparison.Ordinal), s.Start);
    }

    [Fact]
    public void Unc_stops_before_a_following_web_link_or_double_space()
    {
        var spans = LinkDetector.Find("cek \\\\srv\\share\\Folder A lalu https://x.com/a");
        Assert.Equal(2, spans.Count);
        Assert.Equal("\\\\srv\\share\\Folder A lalu", spans[0].Href);
        Assert.Equal("https://x.com/a", spans[1].Href);
        Assert.Equal("\\\\srv\\share\\Folder A", LinkDetector.Find("\\\\srv\\share\\Folder A  catatan").Single().Href);
    }

    [Fact]
    public void Unc_trims_trailing_punctuation_and_space()
    {
        Assert.Equal("\\\\nas01\\share\\folder", Single("path: \\\\nas01\\share\\folder.  ").Href);
    }

    [Fact]
    public void Indonesian_sentence_with_several_links_in_order()
    {
        var text = "Halo Pak, laporannya ada di https://drive.example.co.id/laporan?id=7 dan salinannya di www.example.id/arsip. Kalau ada pertanyaan, email ke it-support@santos.co.id ya, terima kasih!";
        var spans = LinkDetector.Find(text);
        Assert.Collection(spans,
            a => { Assert.Equal(LinkKind.Web, a.Kind); Assert.Equal("https://drive.example.co.id/laporan?id=7", a.Href); },
            b => { Assert.Equal(LinkKind.Web, b.Kind); Assert.Equal("https://www.example.id/arsip", b.Href); },
            c => { Assert.Equal(LinkKind.Email, c.Kind); Assert.Equal("mailto:it-support@santos.co.id", c.Href); });
        foreach (var s in spans) Assert.Equal(s.Display, text.Substring(s.Start, s.Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tidak ada tautan di sini.")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("www.")]
    [InlineData("versi 1.2.3 dan file.txt")]
    [InlineData("https://")]
    public void No_links(string text) => Assert.Empty(LinkDetector.Find(text));
}
