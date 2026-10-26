using IPop.Api.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Notifier 1.3.0: the browser signs the app on the same PC in with a one-time code (grilled 2026-10-26).
public class NotifierLinkTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_code_works_once_and_only_for_a_minute()
    {
        var store = new NotifierLinkStore();
        var user = Guid.NewGuid();
        var code = store.Issue(user, "10.10.6.51", fresh: false, Now);
        Assert.True(code.Length >= 40);
        Assert.Equal(user, store.Redeem(code, Now.AddSeconds(30))?.UserId);
        Assert.Null(store.Redeem(code, Now.AddSeconds(31)));

        var late = store.Issue(user, "10.10.6.51", fresh: false, Now);
        Assert.Null(store.Redeem(late, Now.AddSeconds(61)));
        Assert.Null(store.Redeem("made-up-code", Now));
    }

    [Fact]
    public void Waiting_apps_count_only_on_their_own_address_and_only_while_they_keep_saying_so()
    {
        var store = new NotifierLinkStore();
        store.MarkWaiting("pc-aaaaaaaa", "10.10.6.51", Now);
        Assert.True(store.AnyWaiting("10.10.6.51", Now.AddSeconds(60)));
        Assert.False(store.AnyWaiting("10.10.6.52", Now));
        Assert.False(store.AnyWaiting("10.10.6.51", Now.AddSeconds(80)));
        store.MarkWaiting("pc-aaaaaaaa", "10.10.6.51", Now);
        store.ClearWaiting("pc-aaaaaaaa");
        Assert.False(store.AnyWaiting("10.10.6.51", Now));
    }

    [Fact]
    public void Switching_a_signed_in_app_needs_a_fresh_browser_login_from_the_same_address()
    {
        var user = Guid.NewGuid();
        Assert.True(NotifierLinkEndpoints.CanSwitch(new(user, "10.10.6.51", Now, Fresh: true), "10.10.6.51"));
        Assert.False(NotifierLinkEndpoints.CanSwitch(new(user, "10.10.6.51", Now, Fresh: false), "10.10.6.51"));
        Assert.False(NotifierLinkEndpoints.CanSwitch(new(user, "10.10.6.51", Now, Fresh: true), "10.10.6.77"));
        Assert.False(NotifierLinkEndpoints.CanSwitch(new(user, null, Now, Fresh: true), null));
    }

    [Fact]
    public async Task Fresh_means_the_browser_login_itself_started_within_a_minute()
    {
        using var t = new TestDb();
        var u = t.AddUser("NL1");
        var family = Guid.NewGuid();
        t.Db.AuthSessions.Add(new AuthSession { UserId = u.Id, FamilyId = family, RefreshTokenHash = "h1", DeviceFingerprint = "Edge|Win32", CreatedAt = Now.AddSeconds(-20), ExpiresAt = Now.AddDays(90) });
        // A later rotation of the same login: still the old start time that counts.
        var old = Guid.NewGuid();
        t.Db.AuthSessions.Add(new AuthSession { UserId = u.Id, FamilyId = old, RefreshTokenHash = "h2", DeviceFingerprint = "Edge|Win32", CreatedAt = Now.AddHours(-3), ExpiresAt = Now.AddDays(90) });
        t.Db.AuthSessions.Add(new AuthSession { UserId = u.Id, FamilyId = old, RefreshTokenHash = "h3", DeviceFingerprint = "Edge|Win32", CreatedAt = Now.AddSeconds(-5), ExpiresAt = Now.AddDays(90) });
        // The Notifier's own session never counts as a browser login.
        var app = Guid.NewGuid();
        t.Db.AuthSessions.Add(new AuthSession { UserId = u.Id, FamilyId = app, RefreshTokenHash = "h4", DeviceFingerprint = "notifier:PC1", CreatedAt = Now.AddSeconds(-5), ExpiresAt = Now.AddDays(90) });
        await t.Db.SaveChangesAsync();

        Assert.True(await NotifierLinkEndpoints.FreshLoginAsync(t.Db, u.Id, family.ToString(), Now, default));
        Assert.False(await NotifierLinkEndpoints.FreshLoginAsync(t.Db, u.Id, old.ToString(), Now, default));
        Assert.False(await NotifierLinkEndpoints.FreshLoginAsync(t.Db, u.Id, app.ToString(), Now, default));
        Assert.False(await NotifierLinkEndpoints.FreshLoginAsync(t.Db, u.Id, null, Now, default));
        Assert.False(await NotifierLinkEndpoints.FreshLoginAsync(t.Db, Guid.NewGuid(), family.ToString(), Now, default));
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

    [Theory]
    [InlineData("10.10.6.51", null, "10.10.6.51")]                    // straight to the API: the peer itself
    [InlineData("192.168.2.3", "10.12.4.33", "10.12.4.33")]           // through NPM: X-Real-IP
    [InlineData("10.10.6.99", "6.6.6.6", "10.10.6.99")]               // not a proxy: its header is ignored
    public void Client_address_trusts_forwarded_headers_only_from_the_proxy(string peer, string? realIp, string expected)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
        if (realIp is not null) http.Request.Headers["X-Real-IP"] = realIp;
        Assert.Equal(expected, ClientAddress.Of(http, new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Requests_per_minute_are_capped_per_key()
    {
        var store = new NotifierLinkStore();
        for (var i = 0; i < 5; i++) Assert.True(store.Allow("redeem:1.2.3.4", 5, Now));
        Assert.False(store.Allow("redeem:1.2.3.4", 5, Now.AddSeconds(30)));
        Assert.True(store.Allow("redeem:5.6.7.8", 5, Now));
        Assert.True(store.Allow("redeem:1.2.3.4", 5, Now.AddMinutes(1)));
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
