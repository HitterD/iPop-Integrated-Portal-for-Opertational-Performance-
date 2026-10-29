using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using IPop.Api.Notifier;
using IPop.Application.Auth;
using IPop.Application.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPop.Tests;

/// Notifier browser sign-in (1.4.3, OAuth loopback): a one-time code bound to the app's PKCE challenge and the
/// account that clicked Authorize, plus a short code for pasting, redeemable only with the app's verifier.
public class NotifierLinkTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
    private static (string Verifier, string Challenge) Pkce()
    {
        var v = NotifierLoopback.NewSecret();
        return (v, NotifierLoopback.Challenge(v));
    }

    [Fact]
    public void Authorize_then_redeem_with_the_apps_verifier_signs_in_as_the_authorizing_account()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid();
        var (verifier, challenge) = Pkce();
        var minted = store.Authorize(me, challenge, Now)!;
        Assert.Equal(43, minted.Code.Length);
        Assert.Equal(8, minted.ShortCode.Length);
        var r = store.Redeem(minted.Code, verifier, Now.AddSeconds(5));
        Assert.Equal(NotifierLinkStore.Outcome.Ok, r.Outcome);
        Assert.Equal(me, r.Code!.UserId);
    }

    [Fact]
    public void A_wrong_verifier_is_rejected_and_the_right_one_still_works_until_five_failures_burn_it()
    {
        var store = new NotifierLinkStore();
        var (verifier, challenge) = Pkce();
        var code = store.Authorize(Guid.NewGuid(), challenge, Now)!.Code;
        Assert.Equal(NotifierLinkStore.Outcome.WrongVerifier, store.Redeem(code, NotifierLoopback.NewSecret(), Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.WrongVerifier, store.Redeem(code, null, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Ok, store.Redeem(code, verifier, Now).Outcome);

        var burned = store.Authorize(Guid.NewGuid(), challenge, Now)!.Code;
        for (var i = 0; i < NotifierLinkStore.MaxFailures - 1; i++)
            Assert.Equal(NotifierLinkStore.Outcome.WrongVerifier, store.Redeem(burned, NotifierLoopback.NewSecret(), Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Burned, store.Redeem(burned, NotifierLoopback.NewSecret(), Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.Redeem(burned, verifier, Now).Outcome);   // gone, even with the verifier
    }

    [Fact]
    public void A_code_is_single_use_also_across_its_short_code()
    {
        var store = new NotifierLinkStore();
        var (verifier, challenge) = Pkce();
        var minted = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Ok, store.Redeem(minted.Code, verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.Redeem(minted.Code, verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.RedeemShort(minted.ShortCode, verifier, Now).Outcome);

        var other = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Ok, store.RedeemShort(other.ShortCode, verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.Redeem(other.Code, verifier, Now).Outcome);
    }

    [Fact]
    public void A_code_expires_after_two_minutes()
    {
        var store = new NotifierLinkStore();
        var (verifier, challenge) = Pkce();
        var ok = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Ok, store.Redeem(ok.Code, verifier, Now.AddSeconds(120)).Outcome);
        var late = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Expired, store.Redeem(late.Code, verifier, Now.AddSeconds(121)).Outcome);
        var lateShort = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Expired, store.RedeemShort(lateShort.ShortCode, verifier, Now.AddSeconds(121)).Outcome);
    }

    [Fact]
    public void A_short_code_needs_the_apps_verifier_and_guessing_burns_it()
    {
        var store = new NotifierLinkStore();
        var (verifier, challenge) = Pkce();
        var minted = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        var typed = " " + NotifierLoopback.FormatShortCode(minted.ShortCode).ToLowerInvariant() + " ";
        // Someone who saw the short code but has no verifier gets nothing, and burns it trying.
        for (var i = 0; i < NotifierLinkStore.MaxFailures - 1; i++)
            Assert.Equal(NotifierLinkStore.Outcome.WrongVerifier, store.RedeemShort(typed, NotifierLoopback.NewSecret(), Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Burned, store.RedeemShort(typed, NotifierLoopback.NewSecret(), Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.RedeemShort(typed, verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.Redeem(minted.Code, verifier, Now).Outcome);

        // Typed with any case, spaces or dash, the right app redeems it.
        var again = store.Authorize(Guid.NewGuid(), challenge, Now)!;
        Assert.Equal(NotifierLinkStore.Outcome.Ok, store.RedeemShort(NotifierLoopback.FormatShortCode(again.ShortCode).ToLowerInvariant(), verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.RedeemShort("ABCD-EFG", verifier, Now).Outcome);
        Assert.Equal(NotifierLinkStore.Outcome.Unknown, store.RedeemShort(null, verifier, Now).Outcome);
    }

    [Fact]
    public void Authorize_needs_a_real_challenge_and_the_store_is_bounded()
    {
        var store = new NotifierLinkStore();
        Assert.Null(store.Authorize(Guid.NewGuid(), "", Now));
        Assert.Null(store.Authorize(Guid.NewGuid(), "short", Now));
        var (_, challenge) = Pkce();
        for (var i = 0; i < 5000; i++) Assert.NotNull(store.Authorize(Guid.NewGuid(), challenge, Now));
        Assert.Null(store.Authorize(Guid.NewGuid(), challenge, Now));
        Assert.NotNull(store.Authorize(Guid.NewGuid(), challenge, Now.AddMinutes(3)));   // expired ones are swept
    }

    [Theory]
    [InlineData("http://127.0.0.1:53682/callback", true)]
    [InlineData("http://127.0.0.1:1024/callback", true)]
    [InlineData("http://127.0.0.1:65535/callback", true)]
    [InlineData("https://evil.example/callback", false)]
    [InlineData("https://127.0.0.1:53682/callback", false)]
    [InlineData("http://10.10.6.51:53682/callback", false)]
    [InlineData("http://192.168.127.1:53682/callback", false)]
    [InlineData("http://127.0.0.1.evil.com:53682/callback", false)]
    [InlineData("http://127.0.0.1.evil.com/callback", false)]
    [InlineData("http://127.0.0.1/callback", false)]
    [InlineData("http://127.0.0.1:80/callback", false)]
    [InlineData("http://127.0.0.1:1023/callback", false)]
    [InlineData("http://127.0.0.1:65536/callback", false)]
    [InlineData("http://127.0.0.1:099999/callback", false)]
    [InlineData("http://localhost:53682/callback", false)]
    [InlineData("http://127.0.0.1:53682/callback/", false)]
    [InlineData("http://127.0.0.1:53682/other", false)]
    [InlineData("http://127.0.0.1:53682/callback?x=1", false)]
    [InlineData("http://user@127.0.0.1:53682/callback", false)]
    [InlineData("//127.0.0.1:53682/callback", false)]
    [InlineData("javascript:alert(1)//127.0.0.1:53682/callback", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Redirect_must_be_this_pcs_loopback_callback(string? redirect, bool ok) =>
        Assert.Equal(ok, NotifierLoopback.IsValidRedirect(redirect));

    [Fact]
    public void Short_codes_are_readable_and_normalized()
    {
        for (var i = 0; i < 200; i++)
        {
            var c = NotifierLoopback.NewShortCode();
            Assert.Equal(8, c.Length);
            Assert.All(c, ch => Assert.Contains(ch, NotifierLoopback.ShortAlphabet));
            Assert.Equal(c, NotifierLoopback.NormalizeShortCode(NotifierLoopback.FormatShortCode(c).ToLowerInvariant()));
        }
        Assert.Equal("ABCD-2345", NotifierLoopback.FormatShortCode("ABCD2345"));
        Assert.Null(NotifierLoopback.NormalizeShortCode("ABCD-0001"));   // 0 and 1 are not in the alphabet
        Assert.Equal("SJA-PC", NotifierLoopback.CleanPc("SJA-PC"));
        Assert.Equal("this PC", NotifierLoopback.CleanPc("\n\t"));
        Assert.Equal(64, NotifierLoopback.CleanPc(new string('x', 200)).Length);
    }

    [Fact]
    public void The_store_knows_nothing_about_network_addresses()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Api/Notifier/NotifierLinkStore.cs"));
        Assert.DoesNotContain("Ip", System.Text.RegularExpressions.Regex.Replace(src, @"//.*", ""), StringComparison.Ordinal);
        var hub = File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Api/Realtime/AppHub.cs"));
        Assert.DoesNotContain("NotifierLink", hub);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "src/IPop.Web/Services/NotifierLink.cs")));
        Assert.DoesNotContain("notifierLink", File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Web/wwwroot/js/ipop.js")));
    }

    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "IPop.slnx")) && !Directory.Exists(Path.Combine(d.FullName, "src", "IPop.Api"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public async Task Issued_session_is_a_normal_rotating_session_for_active_accounts_only()
    {
        using var t = new TestDb();
        var u = t.AddUser("NL2");
        var off = t.AddUser("NL3", active: false);
        var tokens = new JwtTokenService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build());
        var wf = new AuthWorkflow(t.Db, new Argon2PasswordHasher(), tokens);

        var s = await wf.IssueSessionAsync(u.Id, "notifier:PC1", "10.10.6.51", "auth.notifier_link", default);
        Assert.NotNull(s);
        Assert.Equal(200, (await wf.RefreshAsync(new IPop.Application.Auth.RefreshRequest(s!.RefreshToken), null, default)).Item3);
        Assert.Null(await wf.IssueSessionAsync(off.Id, "notifier:PC1", null, "auth.notifier_link", default));
        Assert.Null(await wf.IssueSessionAsync(Guid.NewGuid(), "notifier:PC1", null, "auth.notifier_link", default));
    }

    [Fact]
    public void Requests_per_minute_are_capped_per_key()
    {
        var store = new NotifierLinkStore();
        for (var i = 0; i < 5; i++) Assert.True(store.Allow("redeem:pc-1", 5, Now));
        Assert.False(store.Allow("redeem:pc-1", 5, Now.AddSeconds(30)));
        Assert.True(store.Allow("redeem:pc-2", 5, Now));
        Assert.True(store.Allow("redeem:pc-1", 5, Now.AddMinutes(1)));
    }
}

/// The link endpoints over real HTTP (a small host with the API's MapNotifierLink and a header-based test login).
public class NotifierLinkEndpointTests
{
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var nik = Request.Headers["X-Test-Nik"].ToString();
            if (nik.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity([new Claim("nik", nik)], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, Microsoft.Data.Sqlite.SqliteConnection Db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); Http.Dispose(); Db.Dispose(); }
    }

    private static async Task<Host> StartAsync()
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) });
        builder.Services.AddDbContext<IPopDbContext>(o => o.UseSqlite(conn));
        builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
        builder.Services.AddScoped<ITokenService, JwtTokenService>();
        builder.Services.AddScoped<IAuthWorkflow, AuthWorkflow>();
        builder.Services.AddSingleton<NotifierReleases>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireRole("Admin")));
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
            db.Database.EnsureCreated();
            foreach (var nik in new[] { "0001", "0002" })
                db.Users.Add(new User { Id = Guid.NewGuid(), Nik = nik, Name = "USER " + nik, Email = nik + "@t.co", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNotifierLink();
        await app.StartAsync();
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, conn);
    }

    private static HttpRequestMessage As(string nik, HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Test-Nik", nik);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private sealed record Authorized(string Code, string ShortCode, int ExpiresIn);
    private sealed record Session(string AccessToken, string RefreshToken, string Nik, string Name);

    [Fact]
    public async Task Authorize_then_redeem_over_http_and_only_with_the_verifier()
    {
        await using var h = await StartAsync();
        var verifier = NotifierLoopback.NewSecret();
        var body = new { challenge = NotifierLoopback.Challenge(verifier), state = NotifierLoopback.NewSecret(), redirect = "http://127.0.0.1:53682/callback", pc = "SJA-PC" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Http.PostAsJsonAsync("/api/notifier/link/authorize", body)).StatusCode);
        var res = await h.Http.SendAsync(As("0002", HttpMethod.Post, "/api/notifier/link/authorize", body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var a = (await res.Content.ReadFromJsonAsync<Authorized>())!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", a.ShortCode);
        Assert.Equal(120, a.ExpiresIn);

        var wrong = await h.Http.PostAsJsonAsync("/api/notifier/link/redeem", new { code = a.Code, deviceKey = "pc-aaaaaaaa", verifier = NotifierLoopback.NewSecret() });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var ok = await h.Http.PostAsJsonAsync("/api/notifier/link/redeem", new { code = a.Code, deviceKey = "pc-aaaaaaaa", verifier });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var s = (await ok.Content.ReadFromJsonAsync<Session>())!;
        Assert.Equal("0002", s.Nik);                                          // the account that clicked Authorize
        Assert.False(string.IsNullOrEmpty(s.RefreshToken));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsJsonAsync("/api/notifier/link/redeem", new { code = a.Code, deviceKey = "pc-aaaaaaaa", verifier })).StatusCode);
    }

    [Fact]
    public async Task Pasted_short_code_redeems_with_the_verifier()
    {
        await using var h = await StartAsync();
        var verifier = NotifierLoopback.NewSecret();
        var res = await h.Http.SendAsync(As("0001", HttpMethod.Post, "/api/notifier/link/authorize",
            new { challenge = NotifierLoopback.Challenge(verifier), state = NotifierLoopback.NewSecret(), redirect = "http://127.0.0.1:1024/callback", pc = "PC" }));
        var a = (await res.Content.ReadFromJsonAsync<Authorized>())!;
        var r = await h.Http.PostAsJsonAsync("/api/notifier/link/redeem", new { shortCode = a.ShortCode.ToLowerInvariant(), deviceKey = "pc-bbbbbbbb", verifier });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("0001", (await r.Content.ReadFromJsonAsync<Session>())!.Nik);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://10.10.6.51:53682/callback")]
    [InlineData("http://127.0.0.1.evil.com:53682/callback")]
    [InlineData("http://127.0.0.1/callback")]
    public async Task Authorize_refuses_any_redirect_but_the_loopback_callback(string redirect)
    {
        await using var h = await StartAsync();
        var res = await h.Http.SendAsync(As("0001", HttpMethod.Post, "/api/notifier/link/authorize",
            new { challenge = NotifierLoopback.NewSecret(), state = NotifierLoopback.NewSecret(), redirect, pc = "PC" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("POST", "intent")]
    [InlineData("POST", "intent/check")]
    [InlineData("POST", "waiting")]
    [InlineData("POST", "handoff")]
    [InlineData("POST", "poll")]
    [InlineData("POST", "claim")]
    [InlineData("POST", "take")]
    [InlineData("POST", "code")]
    [InlineData("POST", "approve")]
    [InlineData("POST", "deny")]
    [InlineData("POST", "connected")]
    [InlineData("GET", "should")]
    [InlineData("GET", "status?request=x")]
    [InlineData("GET", "request/abc")]
    public async Task Old_apps_hand_off_endpoints_answer_410_with_a_message(string method, string path)
    {
        await using var h = await StartAsync();
        var req = new HttpRequestMessage(new HttpMethod(method), "/api/notifier/link/" + path);
        if (method == "POST") req.Content = JsonContent.Create(new { deviceKey = "pc-aaaaaaaa", requestId = new string('r', 43), challenge = new string('c', 43) });
        var res = await h.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Gone, res.StatusCode);
        Assert.Contains(NotifierLinkEndpoints.OutdatedMessage, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_142_redeem_by_request_id_answers_410_and_link_clear_still_204()
    {
        await using var h = await StartAsync();
        var old = await h.Http.PostAsJsonAsync("/api/notifier/link/redeem", new { deviceKey = "pc-aaaaaaaa", verifier = new string('v', 43), requestId = new string('r', 43) });
        Assert.Equal(HttpStatusCode.Gone, old.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Http.SendAsync(As("0001", HttpMethod.Post, "/api/notifier/link/clear", new { }))).StatusCode);
    }
}

/// The app's loopback listener: one callback with the right state, nothing else.
public class NotifierLoopbackServerTests
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Wrong_state_is_rejected_right_state_fires_the_callback_once_then_it_stops()
    {
        var state = NotifierLoopback.NewSecret();
        var calls = new List<LoopbackCallback>();
        await using var server = LoopbackCallbackServer.Start(state, cb =>
        {
            lock (calls) calls.Add(cb);
            return Task.FromResult(new LoopbackAnswer(LoopbackPages.Success("Bagastyo Putra"), true));
        }, TimeSpan.FromMinutes(1));
        Assert.True(NotifierLoopback.IsValidRedirect(server.RedirectUri));
        var cbUrl = server.RedirectUri;

        var bad = await Http.GetAsync(cbUrl + "?code=abc&state=" + NotifierLoopback.NewSecret());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync(cbUrl + "?code=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"http://127.0.0.1:{server.Port}/favicon.ico")).StatusCode);
        Assert.Empty(calls);

        var ok = await Http.GetAsync(NotifierLoopback.CallbackUrl(cbUrl, state, code: "the-code"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var html = await ok.Content.ReadAsStringAsync();
        Assert.Contains("iPop Notifier is connected", html);
        Assert.Contains("Signed in as <b>Bagastyo Putra</b>", html);
        Assert.Contains("window.close()", html);
        Assert.DoesNotContain("http", html.Replace("http-equiv", ""), StringComparison.OrdinalIgnoreCase); // no external resources
        Assert.Equal("no-store", ok.Headers.CacheControl?.ToString());
        Assert.Equal([new LoopbackCallback("the-code", null)], calls);

        await server.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Http.GetAsync(NotifierLoopback.CallbackUrl(cbUrl, state, code: "again")));
        Assert.Single(calls);
    }

    [Fact]
    public async Task Decline_brings_back_access_denied()
    {
        var state = NotifierLoopback.NewSecret();
        LoopbackCallback? got = null;
        await using var server = LoopbackCallbackServer.Start(state, cb => { got = cb; return Task.FromResult(new LoopbackAnswer(LoopbackPages.Cancelled(), true)); }, TimeSpan.FromMinutes(1));
        var res = await Http.GetAsync(NotifierLoopback.CallbackUrl(server.RedirectUri, state, error: "access_denied"));
        Assert.Contains("Sign-in cancelled", await res.Content.ReadAsStringAsync());
        Assert.Equal(new LoopbackCallback(null, "access_denied"), got);
    }

    [Fact]
    public async Task It_listens_on_loopback_only_and_stops_after_its_lifetime()
    {
        await using var server = LoopbackCallbackServer.Start(NotifierLoopback.NewSecret(), _ => Task.FromResult(new LoopbackAnswer("", true)), TimeSpan.FromMilliseconds(300));
        Assert.InRange(server.Port, 1024, 65535);
        await server.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.Handled);
        var listening = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        Assert.DoesNotContain(listening, ep => ep.Port == server.Port);
    }

    [Fact]
    public void Pages_escape_the_name()
    {
        Assert.Contains("&lt;script&gt;", LoopbackPages.Success("<script>"));
        Assert.DoesNotContain("<script>alert", LoopbackPages.Error("<script>alert(1)</script>"));
    }
}

/// The release channels publish-notifier.ps1 writes: pilot sees the pilot feed, everyone else the released one.
public class NotifierReleasesTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ipop-notifier-rel-" + Guid.NewGuid().ToString("N"));

    public NotifierReleasesTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private NotifierReleases Releases() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifier:ReleasesPath"] = dir }).Build());

    private static string Feed(params string[] versions) =>
        "{\"Assets\":[" + string.Join(",", versions.Select(v => $"{{\"PackageId\":\"iPopNotifier\",\"Version\":\"{v}\",\"Type\":\"Full\",\"FileName\":\"iPopNotifier-{v}-full.nupkg\"}}")) + "]}";

    [Fact]
    public void Before_the_first_manifest_the_public_feed_is_everyones_version()
    {
        File.WriteAllText(Path.Combine(dir, NotifierReleases.AllFeed), Feed("1.2.1", "1.2.2"));
        var s = Releases().Read();
        Assert.Equal("1.2.2", s.All?.Version);
        Assert.Null(s.Pilot);
        var (feed, target) = Releases().FeedFor(pilot: true);
        Assert.Contains("1.2.2", feed);
        Assert.Equal("1.2.2", target?.Version);
    }

    [Fact]
    public void Pilot_release_reaches_only_the_pilot_group()
    {
        File.WriteAllText(Path.Combine(dir, NotifierReleases.AllFeed), Feed("1.2.2"));
        File.WriteAllText(Path.Combine(dir, NotifierReleases.PilotFeed), Feed("1.2.2", "1.3.0"));
        File.WriteAllText(Path.Combine(dir, NotifierReleases.Manifest),
            "{\"all\":{\"version\":\"1.2.2\",\"mandatory\":false},\"pilot\":{\"version\":\"1.3.0\",\"mandatory\":true,\"publishedAt\":\"2026-10-26T09:00:00Z\"}}");

        var (pilotFeed, pilotTarget) = Releases().FeedFor(pilot: true);
        Assert.Contains("1.3.0", pilotFeed);
        Assert.Equal(("1.3.0", true), (pilotTarget?.Version, pilotTarget?.Mandatory == true));

        var (allFeed, allTarget) = Releases().FeedFor(pilot: false);
        Assert.DoesNotContain("1.3.0", allFeed);
        Assert.Equal(("1.2.2", false), (allTarget?.Version, allTarget?.Mandatory == true));
    }

    [Fact]
    public void A_pilot_entry_without_its_feed_falls_back_to_everyones()
    {
        File.WriteAllText(Path.Combine(dir, NotifierReleases.AllFeed), Feed("1.2.2"));
        File.WriteAllText(Path.Combine(dir, NotifierReleases.Manifest), "{\"all\":{\"version\":\"1.2.2\"},\"pilot\":{\"version\":\"1.3.0\"}}");
        var (feed, target) = Releases().FeedFor(pilot: true);
        Assert.DoesNotContain("1.3.0", feed);
        Assert.Equal("1.2.2", target?.Version);
    }

    [Theory]
    [InlineData("1.3.0", true)]
    [InlineData("1.3", false)]
    [InlineData("1.3.0.1", false)]
    [InlineData("../1.3.0", false)]
    [InlineData(null, false)]
    public void Versions_are_three_numbers(string? v, bool ok) => Assert.Equal(ok, NotifierReleases.IsVersion(v));
}
