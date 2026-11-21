using System.Net;
using IPop.Api.Notes;
using IPop.Infrastructure.Links;
using Xunit;
using static IPop.Tests.LinkPreviewFetchTests;

namespace IPop.Tests;

/// A picture pasted from the web into a note is fetched by the server — public addresses only, real pictures only.
public class NotesWebImageImportTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static HttpResponseMessage Bytes(byte[] body, string type)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return r;
    }

    [Fact]
    public async Task Public_picture_is_fetched_and_named_after_the_url()
    {
        using var f = new SafeHttpFetcher(new FakeDns(new() { ["img.example.com"] = ["93.184.216.34"] }), new FakeTransport(_ => Bytes(Png, "image/png")));
        var r = await WebImageImport.FetchAsync(f, "https://img.example.com/photos/Line%20chart.png?x=1", default);
        Assert.NotNull(r);
        Assert.Equal("image/png", r!.ContentType);
        Assert.Equal("Line chart.png", r.FileName);
    }

    [Fact]
    public async Task Internal_address_is_refused_without_a_request()
    {
        var transport = new FakeTransport(_ => Bytes(Png, "image/png"));
        using var f = new SafeHttpFetcher(new FakeDns(new() { ["nas.local"] = ["10.10.6.16"] }), transport);
        Assert.Null(await WebImageImport.FetchAsync(f, "http://nas.local/secret.png", default));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Html_or_svg_or_fake_picture_is_refused()
    {
        using var html = new SafeHttpFetcher(new FakeDns(new() { ["a.com"] = ["93.184.216.34"] }), new FakeTransport(_ => Html("<p>hi</p>")));
        Assert.Null(await WebImageImport.FetchAsync(html, "https://a.com/x.png", default));
        using var svg = new SafeHttpFetcher(new FakeDns(new() { ["a.com"] = ["93.184.216.34"] }), new FakeTransport(_ => Bytes("<svg/>"u8.ToArray(), "image/svg+xml")));
        Assert.Null(await WebImageImport.FetchAsync(svg, "https://a.com/x.svg", default));
        using var fake = new SafeHttpFetcher(new FakeDns(new() { ["a.com"] = ["93.184.216.34"] }), new FakeTransport(_ => Bytes("not a png"u8.ToArray(), "image/png")));
        Assert.Null(await WebImageImport.FetchAsync(fake, "https://a.com/x.png", default));
    }

    [Theory]
    [InlineData("javascript:alert(1)")] [InlineData("file:///c:/x.png")] [InlineData("data:image/png;base64,AAAA")] [InlineData("")] [InlineData(null)]
    public async Task Only_web_addresses(string? url)
    {
        using var f = new SafeHttpFetcher(new FakeDns([]), new FakeTransport(_ => Bytes(Png, "image/png")));
        Assert.Null(await WebImageImport.FetchAsync(f, url, default));
    }

    [Fact]
    public void A_url_without_a_name_gets_a_plain_one()
    {
        Assert.Equal("pasted-image.jpg", WebImageImport.FileNameFor(new Uri("https://x.com/"), "image/jpeg"));
        Assert.Equal("a_b.webp", WebImageImport.FileNameFor(new Uri("https://x.com/a_b<>.gif"), "image/webp"));
    }
}
