using System.Net;
using System.Text.Json;
using IPop.WebHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// IPop.WebHost: Digital Asset Links for the Android TWA and the APK download (Wave 1, W1.7/W1.8).
public class AssetLinksTests
{
    private const string Fp = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Statement_links_the_package_and_fingerprints()
    {
        var json = AssetLinks.Build(Config(("AndroidApp:PackageName", "id.co.santos.ipop"), ("AndroidApp:Sha256CertFingerprints:0", Fp)));
        using var doc = JsonDocument.Parse(json);
        var st = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("delegate_permission/common.handle_all_urls", Assert.Single(st.GetProperty("relation").EnumerateArray()).GetString());
        var target = st.GetProperty("target");
        Assert.Equal("android_app", target.GetProperty("namespace").GetString());
        Assert.Equal("id.co.santos.ipop", target.GetProperty("package_name").GetString());
        Assert.Equal(Fp, Assert.Single(target.GetProperty("sha256_cert_fingerprints").EnumerateArray()).GetString());
    }

    [Fact]
    public void Unconfigured_or_placeholder_fingerprints_give_an_empty_list()
    {
        Assert.Equal("[]", AssetLinks.Build(Config()));
        Assert.Equal("[]", AssetLinks.Build(Config(("AndroidApp:PackageName", "id.co.santos.ipop"))));
        Assert.Contains("id.co.santos.ipop", AssetLinks.Build(Config(("AndroidApp:Sha256CertFingerprints:0", Fp))));   // default package
        Assert.Equal("[]", AssetLinks.Build(Config(("AndroidApp:PackageName", "id.co.santos.ipop"), ("AndroidApp:Sha256CertFingerprints:0", "REPLACE_WITH_RELEASE_KEY_SHA256"))));
    }

    private static async Task<(WebApplication App, HttpClient Http)> Host(string root, params (string, string?)[] cfg)
    {
        var wwwroot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><title>spa</title>");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = wwwroot, EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(cfg.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2)));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        HostPipeline.Configure(app, staticAssets: false);
        await app.StartAsync();
        var url = app.Urls.First();
        return (app, new HttpClient { BaseAddress = new Uri(url) });
    }

    [Fact]
    public async Task Endpoint_is_json_not_the_spa_fallback_and_keeps_the_security_headers()
    {
        var root = Directory.CreateTempSubdirectory("ipop-wh-").FullName;
        var (app, http) = await Host(root, ("AndroidApp:PackageName", "id.co.santos.ipop"), ("AndroidApp:Sha256CertFingerprints:0", Fp));
        await using var _ = app;
        var res = await http.GetAsync("/.well-known/assetlinks.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("id.co.santos.ipop", await res.Content.ReadAsStringAsync());
        Assert.True(res.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        // The SPA fallback still answers everything else.
        Assert.Contains("spa", await http.GetStringAsync("/chat"));
    }

    [Fact]
    public async Task Apk_is_served_when_present_and_404_otherwise()
    {
        var root = Directory.CreateTempSubdirectory("ipop-wh-").FullName;
        var apk = Path.Combine(root, "ipop.apk");
        var (app, http) = await Host(root, ("AndroidApp:ApkPath", apk));
        await using var _ = app;
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/download/ipop.apk"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/download/ipop.apk")).StatusCode);

        await File.WriteAllBytesAsync(apk, [0x50, 0x4B, 3, 4, 1, 2, 3]);
        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/download/ipop.apk"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        var res = await http.GetAsync("/download/ipop.apk");
        Assert.Equal("application/vnd.android.package-archive", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(7, (await res.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Company_root_ca_is_served_for_phones_as_a_ca_certificate()
    {
        var root = Directory.CreateTempSubdirectory("ipop-wh-").FullName;
        var (app, http) = await Host(root);
        await using var _ = app;
        var res = await http.GetAsync("/download/sja-root-ca.crt");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/x-x509-ca-cert", res.Content.Headers.ContentType!.MediaType);
        var pem = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.DoesNotContain("PRIVATE KEY", pem);
        using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        Assert.Contains("CN=SJA-ROOT-CA", cert.Subject);
        Assert.False(cert.HasPrivateKey);
    }

    [Fact]
    public async Task Missing_apk_file_is_404()
    {
        var root = Directory.CreateTempSubdirectory("ipop-wh-").FullName;
        var (app, http) = await Host(root, ("AndroidApp:ApkPath", Path.Combine(root, "missing", "ipop.apk")));
        await using var _ = app;
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/download/ipop.apk")).StatusCode);
    }
}

/// /download/notifier/{file}: only the Velopack feed and the setup, never another file name or a path.
public class NotifierDownloadTests
{
    [Theory]
    [InlineData("releases.win.json", true)]
    [InlineData("RELEASES", true)]
    [InlineData("iPopNotifier-win-Setup.exe", true)]
    [InlineData("iPopNotifier-1.0.1-full.nupkg", true)]
    [InlineData("iPopNotifier-1.0.1-delta.nupkg", true)]
    [InlineData("appsettings.json", false)]
    [InlineData("..\appsettings.json", false)]
    [InlineData("../releases.win.json", false)]
    [InlineData("iPopNotifier-1.0.1-full.nupkg.bak", false)]
    // Per-person channel files go through the API only.
    [InlineData("releases.win.pilot.json", false)]
    [InlineData("release.json", false)]
    [InlineData("other-Setup.exe", false)]
    [InlineData("iPopNotifier..-win-Setup.exe", false)]
    [InlineData("", false)]
    public void Only_feed_and_setup_files(string file, bool allowed) =>
        Assert.Equal(allowed, IPop.WebHost.HostPipeline.NotifierFileAllowed(file));
}
