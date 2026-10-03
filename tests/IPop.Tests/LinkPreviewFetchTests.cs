using System.Net;
using System.Text;
using IPop.Api.Endpoints;
using IPop.Infrastructure.Links;
using Xunit;

namespace IPop.Tests;

/// SSRF guard + fetcher behaviour with a fake DNS and a fake transport — no real network.
public class LinkPreviewFetchTests
{
    internal sealed class FakeDns(Dictionary<string, string[]> map) : IDnsResolver
    {
        public List<string> Lookups { get; } = [];
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Lookups.Add(host);
            if (!map.TryGetValue(host, out var ips)) throw new System.Net.Sockets.SocketException(11001);
            return Task.FromResult(ips.Select(IPAddress.Parse).ToArray());
        }
    }

    internal sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            // The real transport only ever connects to these; make sure they are always attached.
            Assert.True(request.Options.TryGetValue(SafeHttpFetcher.VettedAddresses, out var addrs) && addrs.Length > 0);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("iPopLinkPreview/1.0", request.Headers.UserAgent.ToString());
            return Task.FromResult(respond(request));
        }
    }

    internal static HttpResponseMessage Html(string html, string? charset = "utf-8")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html)) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = charset };
        return r;
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static FetchRequest HtmlReq(string url) =>
        new(new Uri(url), m => m == "text/html", 1024 * 1024, true, "text/html");

    // ── SsrfGuard ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("127.10.0.5")] [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")] [InlineData("10.10.6.13")]
    [InlineData("172.16.0.1")] [InlineData("172.31.255.255")]
    [InlineData("192.168.2.16")]
    [InlineData("169.254.169.254")] [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")] [InlineData("100.127.255.254")]
    [InlineData("224.0.0.1")] [InlineData("239.255.255.250")] [InlineData("255.255.255.255")]
    [InlineData("::1")] [InlineData("::")]
    [InlineData("fc00::1")] [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")] [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:10.0.0.1")] [InlineData("::ffff:169.254.169.254")] [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")] [InlineData("2002:c0a8:0101::1")] [InlineData("::127.0.0.1")]
    public void Private_and_special_addresses_are_blocked(string ip) =>
        Assert.False(SsrfGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("1.1.1.1")] [InlineData("172.32.0.1")] [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")] [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.True(SsrfGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("http://example.com:22/")]
    [InlineData("https://user:pw@example.com/")]
    public void Disallowed_uris(string url) => Assert.False(SsrfGuard.IsAllowedUri(new Uri(url)));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com:443/x")]
    [InlineData("http://example.com:80/x")]
    public void Allowed_uris(string url) => Assert.True(SsrfGuard.IsAllowedUri(new Uri(url)));

    // ── Fetcher ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Public_host_is_fetched()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var transport = new FakeTransport(_ => Html("<title>x</title>"));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/")]
    public async Task Literal_private_ip_is_blocked_without_a_request(string url)
    {
        var transport = new FakeTransport(_ => Html("secret"));
        using var f = new SafeHttpFetcher(new FakeDns([]), transport);
        var r = await f.FetchAsync(HtmlReq(url), default);
        Assert.Equal(FetchOutcome.Blocked, r.Outcome);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Hostname_resolving_to_private_ip_is_blocked()
    {
        var dns = new FakeDns(new() { ["evil.example"] = ["93.184.216.34", "192.168.1.10"] });
        var transport = new FakeTransport(_ => Html("secret"));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(HtmlReq("https://evil.example/"), default);
        Assert.Equal(FetchOutcome.Blocked, r.Outcome);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Redirect_to_private_ip_is_blocked()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"], ["internal.example"] = ["10.1.2.3"] });
        var transport = new FakeTransport(req => req.RequestUri!.Host == "example.com"
            ? Redirect("http://internal.example/admin")
            : Html("secret"));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
        Assert.Equal(FetchOutcome.Blocked, r.Outcome);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Redirect_to_metadata_literal_and_bad_port_and_scheme_are_blocked()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        foreach (var to in new[] { "http://169.254.169.254/", "https://example.com:8443/", "file:///etc/passwd" })
        {
            var transport = new FakeTransport(req => req.RequestUri!.AbsolutePath == "/" ? Redirect(to) : Html("x"));
            using var f = new SafeHttpFetcher(dns, transport);
            var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
            Assert.Equal(FetchOutcome.Blocked, r.Outcome);
            Assert.Single(transport.Requests);
        }
    }

    [Fact]
    public async Task Non_standard_port_and_scheme_are_refused_up_front()
    {
        var transport = new FakeTransport(_ => Html("x"));
        using var f = new SafeHttpFetcher(new FakeDns(new() { ["example.com"] = ["93.184.216.34"] }), transport);
        Assert.Equal(FetchOutcome.BadUrl, (await f.FetchAsync(HtmlReq("https://example.com:8080/"), default)).Outcome);
        Assert.Equal(FetchOutcome.BadUrl, (await f.FetchAsync(HtmlReq("ftp://example.com/"), default)).Outcome);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task At_most_three_redirects_are_followed()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var n = 0;
        var transport = new FakeTransport(_ => Redirect($"/hop{++n}"));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
        Assert.Equal(FetchOutcome.TooManyRedirects, r.Outcome);
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task Relative_redirect_within_public_host_is_followed()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var transport = new FakeTransport(req => req.RequestUri!.AbsolutePath == "/" ? Redirect("/final") : Html("<title>ok</title>"));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.Equal("https://example.com/final", r.FinalUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Wrong_content_type_and_oversize_are_refused()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var json = new FakeTransport(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            r.Content.Headers.ContentType = new("application/json");
            return r;
        });
        using (var f = new SafeHttpFetcher(dns, json))
            Assert.Equal(FetchOutcome.WrongType, (await f.FetchAsync(HtmlReq("https://example.com/"), default)).Outcome);

        var big = new FakeTransport(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) };
            r.Content.Headers.ContentType = new("image/png");
            return r;
        });
        using (var f = new SafeHttpFetcher(dns, big))
        {
            var res = await f.FetchAsync(new FetchRequest(new Uri("https://example.com/i.png"), LinkPreviewEndpoints.IsAcceptedImageType, 1000, false, "image/*"), default);
            Assert.Equal(FetchOutcome.TooLarge, res.Outcome);
        }
    }

    [Fact]
    public async Task Html_is_truncated_at_cap_not_rejected()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var transport = new FakeTransport(_ => Html("<title>t</title>" + new string('x', 5000)));
        using var f = new SafeHttpFetcher(dns, transport);
        var r = await f.FetchAsync(new FetchRequest(new Uri("https://example.com/"), m => m == "text/html", 100, true, "text/html"), default);
        Assert.Equal(FetchOutcome.Ok, r.Outcome);
        Assert.True(r.Truncated);
        Assert.Equal(100, r.Body!.Length);
    }

    [Fact]
    public async Task Slow_server_times_out()
    {
        var dns = new FakeDns(new() { ["example.com"] = ["93.184.216.34"] });
        var slow = new SlowTransport();
        using var f = new SafeHttpFetcher(dns, slow, TimeSpan.FromMilliseconds(100));
        var r = await f.FetchAsync(HtmlReq("https://example.com/"), default);
        Assert.Equal(FetchOutcome.Timeout, r.Outcome);
    }

    private sealed class SlowTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Html("late");
        }
    }

    [Theory]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/webp", true)]
    public void Image_types(string media, bool ok) => Assert.Equal(ok, LinkPreviewEndpoints.IsAcceptedImageType(media));

    [Fact]
    public void Resize_caps_width_and_reencodes_jpeg_and_rejects_svg()
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1200, 400);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
        var jpeg = LinkPreviewEndpoints.Resize(ms.ToArray());
        Assert.NotNull(jpeg);
        var info = SixLabors.ImageSharp.Image.Identify(jpeg!);
        Assert.Equal(600, info.Width);
        Assert.Equal(200, info.Height);
        Assert.Equal("JPEG", info.Metadata.DecodedImageFormat!.Name);

        Assert.Null(LinkPreviewEndpoints.Resize(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")));
    }
}
