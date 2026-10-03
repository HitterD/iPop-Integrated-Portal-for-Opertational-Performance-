using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using IPop.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

/// PushState reliability: typed errors instead of a silent "Off", single-flight reconcile,
/// cross-tab intent generations that beat re-register races, and session-end cleanup that
/// never calls the API without a token. The browser half (push-lifecycle.js) is faked in-process;
/// tests/push-lifecycle.test.cjs covers the real script.
public class PushStateTests
{
    private sealed class FakeBrowser : IJSRuntime
    {
        public string Permission = "granted";
        public bool Secure = true;
        public string? BrowserEndpoint;
        public long Gen;
        public string? IntentAcct;
        public bool IntentOptedOut;
        public object? SubscribeResult = Ok("https://push.test/ep1", created: true);
        public Func<Task>? DuringSubscribe;
        public int Subscribes, Unsubscribes;
        public bool BindingAccepted = true;
        public List<(string? Acct, long Gen, bool OptedOut)> Bindings = [];
        public bool? Handoff;
        public List<string> Calls = [];

        public static PushState.JsSubscribeResult Ok(string ep, bool created) => new(true, null, null, ep, "P", "A", created);
        public static PushState.JsSubscribeResult Fail(string code) => new(false, code, "msg", null, null, null, false);

        public async ValueTask<TValue> InvokeAsync<TValue>(string id, object?[]? args)
        {
            Calls.Add(id);
            object? r = null;
            switch (id)
            {
                case "ipop.desktopAlertsState":
                    r = new PushState.JsLegacyState(Secure, Permission, "unsupported", BrowserEndpoint); break;
                case "ipopPush.hello": r = new PushState.JsHello(1, false, null); break;
                case "ipopPush.acct": r = "acct-" + ((string)args![0]!)[..4]; break;
                case "ipopPush.intent": r = new PushState.JsIntent(Gen, IntentAcct, IntentOptedOut); break;
                case "ipopPush.setIntent":
                    Gen++; IntentAcct = (string?)args![0]; IntentOptedOut = (bool)args[1]!; r = Gen; break;
                case "ipopPush.isCurrent": r = (long)args![0]! == Gen; break;
                case "ipopPush.subscribe":
                    Subscribes++;
                    if (DuringSubscribe is not null) await DuringSubscribe();
                    if (SubscribeResult is PushState.JsSubscribeResult { Ok: true } s) BrowserEndpoint = s.Endpoint;
                    r = SubscribeResult; break;
                case "ipopPush.unsubscribe":
                    Unsubscribes++; r = BrowserEndpoint; BrowserEndpoint = null; break;
                case "ipopPush.sendBinding":
                    Bindings.Add(((string?)args![0], (long)args[1]!, (bool)args[2]!)); r = BindingAccepted; break;
                case "ipopPush.endpointDigest": r = "d"; break;
                case "ipopPush.lastRegistration": r = null; break;
                case "ipopPush.setBackgroundHandoff": Handoff = (bool)args![0]!; break;
            }
            return r is null ? default! : (TValue)r;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<TValue>(id, args);
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(string Path, string? Auth)> Requests = [];
        public HttpStatusCode SubscribeStatus = HttpStatusCode.OK;
        public static readonly Guid DeviceId = Guid.Parse("dddd0000-0000-0000-0000-00000000000d");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Requests.Add((path, req.Headers.Authorization?.Parameter));
            return Task.FromResult(path switch
            {
                "/api/push/config" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { enabled = true, publicKey = "BAAB" }) },
                "/api/push/subscriptions" => new HttpResponseMessage(SubscribeStatus)
                { Content = JsonContent.Create(new { id = DeviceId }) },
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            });
        }
    }

    private static readonly Guid UserA = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");

    private static (PushState Push, FakeBrowser Js, FakeApi Api, AuthState Auth) Rig(Guid? user = null)
    {
        var js = new FakeBrowser();
        var api = new FakeApi();
        var http = new HttpClient(api) { BaseAddress = new Uri("https://ipop.test/") };
        var auth = new AuthState(http, js);
        SignIn(auth, user ?? UserA);
        return (new PushState(auth, http, js), js, api, auth);
    }

    private static void SignIn(AuthState auth, Guid user)
    {
        typeof(AuthState).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(auth, "tok");
        var me = new DirectoryEmployeeDto("100", "Jane", null, null, null, null, null, null, "j@x", null, null, "online", UserId: user);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, me);
    }

    private static void RaiseSignedOut(AuthState auth)
    {
        typeof(AuthState).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(auth, null);
        var f = typeof(AuthState).GetField("SignedOut", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((Action?)f.GetValue(auth))?.Invoke();
    }

    [Fact]
    public async Task Registration_failure_is_exposed_not_silently_off()
    {
        var (push, js, _, _) = Rig();
        js.SubscribeResult = FakeBrowser.Fail("subscribe_abort");
        await push.StartAsync();

        Assert.False(push.Subscribed);
        Assert.Equal(PushStatus.Error, push.Status);
        Assert.Equal("subscribe_abort", push.LastError!.Code);
        Assert.Contains("AbortError", push.LastError.Message);
        Assert.False(push.BackgroundHandoff);
    }

    [Fact]
    public async Task Start_registers_binds_the_worker_and_enables_handoff()
    {
        var (push, js, api, _) = Rig();
        await push.StartAsync();

        Assert.True(push.Subscribed);
        Assert.Equal(PushStatus.On, push.Status);
        Assert.Null(push.LastError);
        Assert.Contains(api.Requests, r => r.Path == "/api/push/subscriptions" && r.Auth == "tok");
        Assert.Equal(("acct-aaaa", js.Gen, false), js.Bindings[^1]);
        Assert.True(push.BackgroundHandoff);
        Assert.True(js.Handoff);
        Assert.Equal(FakeApi.DeviceId, push.CurrentDeviceId);
    }

    [Fact]
    public async Task Server_rejection_is_typed()
    {
        var (push, js, api, _) = Rig();
        api.SubscribeStatus = HttpStatusCode.BadRequest;
        await push.StartAsync();
        Assert.Equal("server_rejected", push.LastError!.Code);
        Assert.False(push.Subscribed);
    }

    [Fact]
    public async Task Opt_out_in_another_tab_during_subscribe_wins_and_the_run_undoes_itself()
    {
        var (push, js, api, _) = Rig();
        // The other tab opts out while this one is waiting on the browser's push service.
        js.DuringSubscribe = () => { js.Gen++; js.IntentOptedOut = true; return Task.CompletedTask; };
        await push.StartAsync();

        Assert.False(push.Subscribed);
        Assert.Null(js.BrowserEndpoint);
        Assert.Equal(1, js.Unsubscribes);
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/push/subscriptions");
        Assert.Contains(api.Requests, r => r.Path == "/api/push/subscriptions/remove");
        Assert.DoesNotContain(js.Bindings, b => b is { OptedOut: false, Acct: not null } && b.Gen == js.Gen);
    }

    [Fact]
    public async Task Focus_check_never_reclaims_after_another_tab_signed_out()
    {
        var (push, js, api, _) = Rig();
        await push.StartAsync();
        Assert.True(push.Subscribed);

        // Another tab signs out: the shared intent now says nobody owns push here.
        js.Gen++; js.IntentAcct = null; js.BrowserEndpoint = null;
        await push.OnPushIntentChanged();
        var subscribesBefore = js.Subscribes;
        await push.CheckAsync("focus", force: true);

        Assert.False(push.Subscribed);
        Assert.Equal(subscribesBefore, js.Subscribes);
        Assert.Null(js.IntentAcct);
        Assert.False(push.BackgroundHandoff);
    }

    [Fact]
    public async Task Concurrent_triggers_share_one_reconcile()
    {
        var (push, js, _, _) = Rig();
        var gate = new TaskCompletionSource();
        js.DuringSubscribe = () => gate.Task;
        await push.StartAsync().WaitAsync(TimeSpan.FromMilliseconds(50)).ContinueWith(_ => { });
        var a = push.CheckAsync("focus");
        var b = push.CheckAsync("online");
        var c = push.CheckAsync("visible");
        Assert.True(push.Checking);
        gate.SetResult();
        await Task.WhenAll(a, b, c);
        Assert.Equal(1, js.Subscribes);
    }

    [Fact]
    public async Task Session_end_cleans_up_locally_without_calling_the_api()
    {
        var (push, js, api, auth) = Rig();
        await push.StartAsync();
        api.Requests.Clear();

        RaiseSignedOut(auth);
        await push.OnSignedOutAsync(); // the event handler is fire-and-forget; await the same work

        Assert.Empty(api.Requests);
        Assert.Null(js.BrowserEndpoint);
        Assert.Null(js.IntentAcct);
        Assert.Equal((string?)null, js.Bindings[^1].Acct);
        Assert.False(push.Subscribed);
        Assert.False(js.Handoff);
        Assert.Null(push.CurrentDeviceId);
    }

    [Fact]
    public async Task Explicit_sign_out_removes_on_the_server_while_the_token_is_still_valid()
    {
        var (push, js, api, _) = Rig();
        await push.StartAsync();
        await push.SignOutAsync();

        Assert.Contains(api.Requests, r => r.Path == "/api/push/subscriptions/remove" && r.Auth == "tok");
        Assert.Null(js.IntentAcct);
        Assert.Null(js.Bindings[^1].Acct);
    }

    [Fact]
    public async Task Disable_persists_opt_out_and_start_respects_it()
    {
        var (push, js, _, _) = Rig();
        await push.StartAsync();
        await push.DisableAsync();
        Assert.True(push.OptedOut);
        Assert.Equal(PushStatus.Off, push.Status);
        Assert.True(js.Bindings[^1].OptedOut);

        var subscribes = js.Subscribes;
        await push.StartAsync();
        Assert.Equal(subscribes, js.Subscribes);
        Assert.False(push.Subscribed);
    }

    [Fact]
    public async Task Account_switch_claims_the_intent_for_the_new_account()
    {
        var (push, js, _, auth) = Rig(UserA);
        await push.StartAsync();
        SignIn(auth, UserB);
        await push.StartAsync();
        Assert.Equal("acct-bbbb", js.IntentAcct);
        Assert.Equal("acct-bbbb", js.Bindings[^1].Acct);
    }

    [Fact]
    public async Task Repair_message_forces_re_registration()
    {
        var (push, js, api, _) = Rig();
        await push.StartAsync();
        api.Requests.Clear();
        await push.OnPushRepair();
        Assert.Contains(api.Requests, r => r.Path == "/api/push/subscriptions");
        Assert.False(push.RepairNeeded);
        Assert.Contains("ipopPush.repairDone", js.Calls);
    }

    [Fact]
    public async Task Unacked_binding_from_an_old_worker_keeps_page_sounds()
    {
        var (push, js, _, _) = Rig();
        js.BindingAccepted = false; // old sw.js: no reply
        await push.StartAsync();
        Assert.True(push.Subscribed);
        Assert.False(push.BackgroundHandoff);
    }

    [Theory]
    [InlineData("subscribe_abort")]
    [InlineData("weird_new_code")]
    public void PushError_never_carries_urls(string code)
    {
        var e = PushError.From(code, "failed for https://fcm.googleapis.com/fcm/send/abc");
        Assert.DoesNotContain("://", e.Message);
    }
}
