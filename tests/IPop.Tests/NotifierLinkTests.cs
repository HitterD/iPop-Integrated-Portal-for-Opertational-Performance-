using IPop.Api.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Notifier browser sign-in: a one-time, PKCE-bound code (1.3.0) approved on a connect card in the browser (1.4.2).
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

    // ── 1.4.2: a connect card in the browser, approved by a click; no protocol launch ─────────────────────

    private const string Ip = "10.10.6.51";
    private const string Pc = "pc-aaaaaaaa";
    private static string S(char c) => new(c, 43);

    /// The app's side: a request id + verifier it keeps, the challenge it sends.
    private static (string Request, string Verifier, NotifierLinkStore.ConnectRequest R) Click(NotifierLinkStore store, DateTime at,
        string device = Pc, string ip = Ip, char seed = 'a')
    {
        var request = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var verifier = new string(seed, 43);
        var r = store.Register(device, ip, request, NotifierLinkStore.Challenge(verifier), "SJA-PC", "ipop-notifier", at)!;
        return (request, verifier, r);
    }

    [Fact]
    public void Intent_then_Connect_then_poll_then_redeem_signs_the_app_in_as_the_approving_account()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid();
        var (request, verifier, r) = Click(store, Now);
        Assert.Equal("SJA-PC", r.PcName);
        Assert.Equal("pending", store.Poll(Pc, request, reserve: false, Now.AddSeconds(1))!.State);
        Assert.Equal(request, store.Claim(Ip, Now.AddSeconds(1))?.Id);                      // the open tab shows the card
        Assert.Equal("claimed", store.Poll(Pc, request, false, Now.AddSeconds(2))!.State);
        Assert.Equal(NotifierLinkStore.Decision.Ok, store.Approve(request, me, Ip, Now.AddSeconds(10)));
        var poll = store.Poll(Pc, request, false, Now.AddSeconds(11))!;
        Assert.Equal("approved", poll.State);
        Assert.Equal(43, poll.Code!.Length);
        // The code is handed out once; a second poll only learns it was collected.
        Assert.Equal(new NotifierLinkStore.PollResult("collected", null), store.Poll(Pc, request, false, Now.AddSeconds(12)));
        // Only the app's verifier redeems it.
        Assert.Null(store.Redeem(poll.Code, S('x'), request, Now.AddSeconds(12)));
        Assert.Equal(me, store.Redeem(poll.Code, verifier, request, Now.AddSeconds(13))?.UserId);
        Assert.Null(store.Redeem(poll.Code, verifier, request, Now.AddSeconds(14)));        // single use
        Assert.True(store.MarkConnected(request, me, "Bagastyo", Now.AddSeconds(14)));
        Assert.Equal("Bagastyo", store.Status(request, me, Now.AddSeconds(15))?.Name);
        Assert.Null(store.Status(request, Guid.NewGuid(), Now.AddSeconds(15)));           // nobody else reads it
    }

    [Fact]
    public void A_request_needs_the_apps_own_secrets_and_a_known_scheme()
    {
        var store = new NotifierLinkStore();
        Assert.Null(store.Register(Pc, Ip, "short", S('c'), "PC", "ipop-notifier", Now));
        Assert.Null(store.Register(Pc, Ip, S('r'), null, "PC", "ipop-notifier", Now));
        Assert.Null(store.Register(Pc, Ip, S('r'), S('c'), "PC", "https", Now));
        Assert.NotNull(store.Register(Pc, Ip, S('r'), S('c'), "<b>PC</b>", "ipop-notifier-qa", Now));
        Assert.Equal("bPCb", store.Find(S('r'))!.PcName);                                  // shown as text, cleaned
        Assert.Null(store.Register("pc-bbbbbbbb", Ip, S('r'), S('c'), "PC", "ipop-notifier", Now)); // an id is used once
        Assert.Equal("this PC", NotifierLinkStore.CleanPcName(null));
    }

    [Fact]
    public void A_request_expires_after_two_minutes()
    {
        var store = new NotifierLinkStore();
        var (request, _, _) = Click(store, Now);
        Assert.Null(store.Claim(Ip, Now.AddSeconds(121)));
        Assert.Equal(NotifierLinkStore.Decision.Expired, store.Approve(request, Guid.NewGuid(), Ip, Now.AddSeconds(121)));
        Assert.Equal(NotifierLinkStore.Decision.Expired, store.Deny(request, Ip, Now.AddSeconds(121)));
        Assert.Equal("expired", store.Poll(Pc, request, false, Now.AddSeconds(121))!.State);
        // Still remembered for a while (the card says "expired"), then gone.
        Assert.NotNull(store.Find(request));
        Click(store, Now.AddMinutes(11), device: "pc-cccccccc");                          // sweeps
        Assert.Null(store.Find(request));
    }

    [Fact]
    public void Only_a_browser_on_the_apps_address_can_approve_or_cancel()
    {
        var store = new NotifierLinkStore();
        var (request, _, _) = Click(store, Now);
        Assert.Null(store.Claim("10.10.6.52", Now));                                       // another PC's tabs never claim it
        Assert.Equal(NotifierLinkStore.Decision.OtherAddress, store.Approve(request, Guid.NewGuid(), "10.10.6.52", Now));
        Assert.Equal(NotifierLinkStore.Decision.OtherAddress, store.Approve(request, Guid.NewGuid(), null, Now));
        Assert.Equal(NotifierLinkStore.Decision.OtherAddress, store.Deny(request, "10.10.6.52", Now));
        Assert.Equal("pending", store.Poll(Pc, request, false, Now)!.State);
        Assert.Equal(NotifierLinkStore.Decision.NotFound, store.Approve(S('z'), Guid.NewGuid(), Ip, Now));
    }

    [Fact]
    public void A_request_is_answered_once()
    {
        var store = new NotifierLinkStore();
        var me = Guid.NewGuid(); var other = Guid.NewGuid();
        var (request, _, _) = Click(store, Now);
        Assert.Equal(NotifierLinkStore.Decision.Ok, store.Approve(request, me, Ip, Now));
        Assert.Equal(NotifierLinkStore.Decision.Used, store.Approve(request, other, Ip, Now));  // a second tab / account
        Assert.Equal(NotifierLinkStore.Decision.Used, store.Approve(request, me, Ip, Now));
        Assert.Equal(NotifierLinkStore.Decision.Used, store.Deny(request, Ip, Now));
        Assert.Equal(me, store.Find(request)!.ApprovedBy);
    }

    [Fact]
    public void Cancel_tells_the_app_and_mints_nothing()
    {
        var store = new NotifierLinkStore();
        var (request, _, _) = Click(store, Now);
        store.Claim(Ip, Now);
        Assert.Equal(NotifierLinkStore.Decision.Ok, store.Deny(request, Ip, Now.AddSeconds(3)));
        Assert.Equal(new NotifierLinkStore.PollResult("denied", null), store.Poll(Pc, request, false, Now.AddSeconds(4)));
        Assert.Equal(NotifierLinkStore.Decision.Used, store.Approve(request, Guid.NewGuid(), Ip, Now.AddSeconds(5)));
        Assert.Null(store.Status(request, Guid.NewGuid(), Now.AddSeconds(5)));
    }

    [Fact]
    public void Only_the_device_that_asked_can_poll_its_request()
    {
        var store = new NotifierLinkStore();
        var (request, _, _) = Click(store, Now);
        store.Approve(request, Guid.NewGuid(), Ip, Now);
        Assert.Null(store.Poll("pc-bbbbbbbb", request, false, Now));                       // wrong device key: no code
        Assert.Null(store.Poll(Pc, S('q'), false, Now));
        Assert.Null(store.Poll(Pc, "short", false, Now));
        Assert.Equal("approved", store.Poll(Pc, request, false, Now)!.State);              // still there for the right one
    }

    [Fact]
    public void Exactly_one_tab_claims_a_click_and_after_a_reserve_none_does()
    {
        var store = new NotifierLinkStore();
        var (first, _, _) = Click(store, Now);
        Assert.Equal(first, store.Claim(Ip, Now)?.Id);
        Assert.Null(store.Claim(Ip, Now));                                                 // the second tab stands down
        // A new click (Open browser again) is a new request; the device's open one is replaced.
        var (again, _, _) = Click(store, Now.AddSeconds(25), seed: 'b');
        Assert.Null(store.Find(first));
        // No tab took it within ~5 s: the app reserves it and opens one tab on the card itself.
        Assert.Equal("pending", store.Poll(Pc, again, reserve: true, Now.AddSeconds(30))!.State);
        Assert.Null(store.Claim(Ip, Now.AddSeconds(31)));
        // The tab the app opened goes straight to the card and can still approve it.
        Assert.Equal(NotifierLinkStore.Decision.Ok, store.Approve(again, Guid.NewGuid(), Ip, Now.AddSeconds(40)));
        // Two PCs behind one address (NAT) each keep their own request.
        var (mine, _, _) = Click(store, Now.AddSeconds(50), device: "pc-dddddddd");
        var (theirs, _, _) = Click(store, Now.AddSeconds(51), device: "pc-eeeeeeee");
        Assert.NotNull(store.Find(mine));
        Assert.Equal(theirs, store.Claim(Ip, Now.AddSeconds(52))?.Id);                    // newest first
        Assert.Equal(mine, store.Claim(Ip, Now.AddSeconds(52))?.Id);
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
