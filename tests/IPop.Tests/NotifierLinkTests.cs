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
        var verifier = new string('v', 43);
        var request = new string('r', 43);
        var code = store.Issue(user, NotifierLinkStore.Challenge(verifier), request, Now)!;
        Assert.True(code.Length >= 40);
        Assert.Null(store.Redeem(code, new string('x', 43), request, Now));
        Assert.Null(store.Redeem(code, verifier, new string('x', 43), Now));
        Assert.Equal(user, store.Redeem(code, verifier, request, Now.AddSeconds(30))?.UserId);
        Assert.Null(store.Redeem(code, verifier, request, Now.AddSeconds(31)));

        var late = store.Issue(user, NotifierLinkStore.Challenge(verifier), request, Now);
        Assert.Null(store.Redeem(late, verifier, request, Now.AddSeconds(61)));
        Assert.Null(store.Redeem("made-up-code", verifier, request, Now));
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
    public void Code_issuance_requires_fixed_size_pkce_and_request_secrets_and_is_bounded()
    {
        var store = new NotifierLinkStore();
        var user = Guid.NewGuid();
        Assert.Null(store.Issue(user, "", "", Now));
        var secret = new string('a', 43);
        for (var i = 0; i < 5000; i++) Assert.NotNull(store.Issue(user, secret, secret, Now));
        Assert.Null(store.Issue(user, secret, secret, Now));
        Assert.NotNull(store.Issue(user, secret, secret, Now.AddSeconds(61)));
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

    // ── 1.4.0: one tab, one prompt, only when asked for ─────────────────────────────────────────────────

    private const string Ip = "10.10.6.51";
    private const string Pc = "pc-aaaaaaaa";
    private static string S(char c) => new(c, 43);

    [Fact]
    public void A_waiting_app_alone_no_longer_lets_the_browser_prompt_only_a_click_does_for_two_minutes()
    {
        var store = new NotifierLinkStore();
        store.MarkWaiting(Pc, Ip, Now);
        Assert.False(store.HasFreshIntent(Ip, Now));

        var intent = store.RegisterIntent(Pc, Ip, "ipop-notifier", Now)!;
        Assert.Equal(43, intent.Id.Length);
        Assert.True(store.HasFreshIntent(Ip, Now.AddSeconds(119)));
        Assert.False(store.HasFreshIntent("10.10.6.52", Now));
        Assert.False(store.HasFreshIntent(Ip, Now.AddSeconds(121)));
        Assert.Null(store.ClaimIntent(Ip, opened: false, Guid.NewGuid(), Now.AddSeconds(121)));
        Assert.Null(store.RegisterIntent(Pc, Ip, "https", Now));
    }

    [Fact]
    public async Task Should_requires_a_fresh_intent_and_keeps_only_the_guarded_account_switch()
    {
        using var t = new TestDb();
        var me = t.AddUser("NL4");
        var other = t.AddUser("NL5");
        var store = new NotifierLinkStore();
        var now = DateTime.UtcNow;
        store.MarkWaiting(Pc, Ip, now);
        // The 1.3.0 "any waiting app here → prompt" is gone.
        Assert.Null(await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, Ip, null, opened: false, now, default));
        // A 1.3.0 app's own "Sign in with browser" tab still works while it waits.
        Assert.Equal("legacy", await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, Ip, null, opened: true, now, default));
        store.RegisterIntent(Pc, Ip, "ipop-notifier", now);
        Assert.Equal("intent", await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, Ip, null, opened: false, now, default));
        Assert.Null(await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, Ip, null, opened: false, now.AddMinutes(3), default));

        // Web account switch: only straight after this browser's own login, and the app confirms locally.
        t.Db.NotifierDevices.Add(new NotifierDevice { UserId = other.Id, DeviceKey = "pc-bbbbbbbb", MachineName = "PC1", RemoteIp = "10.10.6.60", LastSeenAt = now });
        var fresh = Guid.NewGuid(); var old = Guid.NewGuid();
        t.Db.AuthSessions.Add(new AuthSession { UserId = me.Id, FamilyId = fresh, RefreshTokenHash = "f1", DeviceFingerprint = "Edge|Win32", CreatedAt = now.AddSeconds(-10), ExpiresAt = now.AddDays(90) });
        t.Db.AuthSessions.Add(new AuthSession { UserId = me.Id, FamilyId = old, RefreshTokenHash = "f2", DeviceFingerprint = "Edge|Win32", CreatedAt = now.AddHours(-2), ExpiresAt = now.AddDays(90) });
        await t.Db.SaveChangesAsync();
        Assert.Equal("other", await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, "10.10.6.60", fresh.ToString(), false, now, default));
        Assert.Null(await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, me.Id, "10.10.6.60", old.ToString(), false, now, default));
        Assert.Null(await NotifierLinkEndpoints.ShouldReasonAsync(store, t.Db, other.Id, "10.10.6.60", fresh.ToString(), false, now, default));
    }

    [Fact]
    public void Exactly_one_tab_claims_a_click_and_the_app_sees_it()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid();
        var intent = store.RegisterIntent(Pc, Ip, "ipop-notifier-qa", Now)!;
        Assert.False(store.CheckIntent(Pc, intent.Id, reserve: false, Now));
        Assert.Null(store.ClaimIntent("10.10.6.52", false, me, Now));                 // another PC never
        var won = store.ClaimIntent(Ip, false, me, Now.AddSeconds(1));
        Assert.Equal("ipop-notifier-qa", won?.Scheme);
        Assert.Null(store.ClaimIntent(Ip, false, Guid.NewGuid(), Now.AddSeconds(1)));   // the second tab stands down
        Assert.True(store.CheckIntent(Pc, intent.Id, reserve: true, Now.AddSeconds(2)));
        Assert.Null(store.CheckIntent(Pc, S('x'), false, Now));                        // someone else's id
        Assert.Null(store.CheckIntent("pc-cccccccc", intent.Id, false, Now));
    }

    [Fact]
    public void When_no_tab_answers_the_app_reserves_the_click_for_the_one_tab_it_opens()
    {
        var store = new NotifierLinkStore();
        var intent = store.RegisterIntent(Pc, Ip, "ipop-notifier", Now)!;
        Assert.False(store.CheckIntent(Pc, intent.Id, reserve: true, Now.AddSeconds(5)));
        // A tab that only now hears about it doesn't take it (the app is already opening one) …
        Assert.Null(store.ClaimIntent(Ip, opened: false, Guid.NewGuid(), Now.AddSeconds(6)));
        // … the tab the app opened does.
        Assert.NotNull(store.ClaimIntent(Ip, opened: true, Guid.NewGuid(), Now.AddSeconds(8)));
        Assert.Null(store.ClaimIntent(Ip, opened: true, Guid.NewGuid(), Now.AddSeconds(9)));
        // "Open browser again" starts a fresh attempt.
        var again = store.RegisterIntent(Pc, Ip, "ipop-notifier", Now.AddSeconds(30))!;
        Assert.NotEqual(intent.Id, again.Id);
        Assert.Null(store.CheckIntent(Pc, intent.Id, false, Now.AddSeconds(30)));
        Assert.False(store.CheckIntent(Pc, again.Id, false, Now.AddSeconds(30)));
    }

    [Fact]
    public void The_app_hands_its_pkce_request_only_to_the_tab_that_claimed_its_click()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var intent = store.RegisterIntent(Pc, Ip, "ipop-notifier", Now)!;
        var (nonce, request, challenge) = (S('n'), S('r'), S('c'));
        // Unclaimed (no tab took the click): nothing to hand to.
        Assert.False(store.PutHandoff(Pc, intent.Id, nonce, request, challenge, Now));
        store.ClaimIntent(Ip, false, me, Now);
        Assert.False(store.PutHandoff(Pc, S('x'), nonce, request, challenge, Now));    // not the app's intent
        Assert.True(store.PutHandoff(Pc, intent.Id, nonce, request, challenge, Now));
        // One answer per click: a second prepare (another page launching its own nonce) gets nothing.
        Assert.False(store.PutHandoff(Pc, intent.Id, S('m'), S('q'), challenge, Now));

        Assert.Null(store.TakeHandoff(S('m'), me, Ip, Now));                          // wrong nonce
        Assert.Null(store.TakeHandoff(nonce, stranger, Ip, Now));                     // other account
        Assert.Null(store.TakeHandoff(nonce, me, "10.10.6.52", Now));                 // other address
        var h = store.TakeHandoff(nonce, me, Ip, Now.AddSeconds(2));
        Assert.Equal(request, h?.RequestId);
        Assert.Equal(challenge, h?.Challenge);
        Assert.Null(store.TakeHandoff(nonce, me, Ip, Now.AddSeconds(3)));              // single use
    }

    [Fact]
    public void A_hand_off_expires_after_a_minute()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid();
        var intent = store.RegisterIntent(Pc, Ip, "ipop-notifier", Now)!;
        store.ClaimIntent(Ip, false, me, Now);
        Assert.True(store.PutHandoff(Pc, intent.Id, S('n'), S('r'), S('c'), Now));
        Assert.Null(store.TakeHandoff(S('n'), me, Ip, Now.AddSeconds(61)));
    }

    [Fact]
    public void The_tab_learns_the_app_is_connected_only_from_the_account_it_minted_for()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid(); var other = Guid.NewGuid();
        var verifier = S('v'); var request = S('r');
        Assert.False(store.MarkConnected(request, me, "Me", Now));                     // nothing minted yet
        var code = store.Issue(me, NotifierLinkStore.Challenge(verifier), request, Now)!;
        Assert.Equal(new NotifierLinkStore.LinkStatus(me, Now, null, null), store.Status(request, me, Now));
        Assert.Null(store.Status(request, other, Now));
        Assert.NotNull(store.Redeem(code, verifier, request, Now.AddSeconds(5)));
        // A cancelled account switch revokes the session as the other account; it can't confirm this request.
        Assert.False(store.MarkConnected(request, other, "Other", Now.AddSeconds(6)));
        Assert.Null(store.Status(request, me, Now.AddSeconds(6))!.ConnectedAt);
        Assert.True(store.MarkConnected(request, me, "Bagastyo", Now.AddSeconds(6)));
        var st = store.Status(request, me, Now.AddSeconds(7))!;
        Assert.Equal("Bagastyo", st.Name);
        Assert.NotNull(st.ConnectedAt);
        // Another account minting for the same request id does not take it over.
        store.Issue(other, NotifierLinkStore.Challenge(verifier), request, Now.AddSeconds(8));
        Assert.Equal(me, store.Status(request, me, Now.AddSeconds(8))?.UserId);
        Assert.Null(store.Status(request, me, Now.AddMinutes(4)));
    }

    [Fact]
    public void Open_tabs_are_found_by_their_address()
    {
        var store = new NotifierLinkStore();
        store.TabConnected("c1", Ip);
        store.TabConnected("c2", "10.10.6.52");
        store.TabConnected("c3", null);
        Assert.Equal(["c1"], store.TabsAt(Ip));
        store.TabDisconnected("c1");
        Assert.Empty(store.TabsAt(Ip));
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
