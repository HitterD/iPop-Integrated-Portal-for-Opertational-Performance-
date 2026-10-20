using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Windows toasts through Web Push: this browser's permission + subscription, kept in step
/// with /api/push. The service worker (wwwroot/sw.js) shows the toasts; nothing here does.
///
/// Reliability rules (wwwroot/js/push-lifecycle.js holds the browser half):
///  - Reconcile is single-flight. It runs on start, focus, online, when the tab becomes visible,
///    and every 30 minutes; event-driven checks are throttled, and it never prompts.
///  - Registration failures are kept as a typed, sanitised <see cref="LastError"/>, so the
///    toggle no longer silently reads "Off".
///  - Opt-out, sign-out and the account switch are a cross-tab "intent" with a global monotonic
///    generation. A reconcile that loses a race to a newer intent undoes itself instead of
///    re-registering over a disable.
///  - Session end (refresh refused) does no authenticated cleanup. It unbinds the worker
///    (acct null, so pushes are suppressed) and drops the browser subscription; the server
///    then gets 404/410 on the next send and forgets the row.
public sealed class PushState : IDisposable
{
    private const string BannerKey = "ipop-push-banner-dismissed";
    private static readonly TimeSpan BannerSnooze = TimeSpan.FromDays(7);
    /// Event-driven checks (focus/online/visible) closer together than this are skipped.
    internal static TimeSpan MinCheckInterval = TimeSpan.FromSeconds(60);
    /// Re-POST the subscription at least this often, so LastSeenAt and ApiBase stay fresh.
    internal static readonly TimeSpan ServerRefresh = TimeSpan.FromHours(12);

    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private readonly NotificationState? _notifs;
    private DotNetObjectReference<PushState>? _self;

    public PushState(AuthState auth, HttpClient http, IJSRuntime js, NotificationState? notifs = null)
    {
        _auth = auth;
        _http = http;
        _js = js;
        _notifs = notifs;
        _auth.SignedOut += OnSignedOut;
        if (_notifs is not null) _notifs.PreferencesChanged += OnPrefsChanged;
    }

    /// "default" | "granted" | "denied" | "unsupported" (plain http, or an old browser).
    public string Permission { get; private set; } = "unsupported";
    public bool Secure { get; private set; }
    /// Push is configured on the server (VAPID keys present).
    public bool ServerEnabled { get; private set; }
    /// This browser has a live subscription registered with the API for the signed-in account.
    public bool Subscribed => Endpoint is not null && !OptedOut;
    /// Raw endpoint, only for matching "this device" in the device list. Never display or log it.
    public string? Endpoint { get; private set; }
    public string Idle { get; private set; } = "unsupported";
    public bool BannerVisible { get; private set; }

    /// The user turned toasts off in this browser profile (shared by every tab and account here).
    public bool OptedOut { get; private set; }
    /// The last registration problem, or null once registration succeeds.
    public PushError? LastError { get; private set; }
    public DateTime? LastCheckedAt { get; private set; }
    /// The worker saw pushsubscriptionchange and the page has not re-registered yet.
    public bool RepairNeeded { get; private set; }
    /// The service-worker message schema: 0 for an old sw.js that does not answer hello.
    public int SwSchema { get; private set; }
    /// The worker confirmed it stored this account's binding, so it may show background toasts.
    public bool BindingAcked { get; private set; }
    /// What the worker last did with a push (from hello). "notification-created" means
    /// showNotification resolved, not that Windows displayed the toast.
    public PushWorkerResult? LastWorkerResult { get; private set; }
    /// The worker owns background alerts and their sound, so the page may skip its own sound
    /// while hidden. Mirrored into window.ipopPushBackgroundHandoff.
    public bool BackgroundHandoff => Subscribed && Permission == "granted" && SwSchema >= 1 && BindingAcked;
    /// The server's id for this browser's subscription (POST /api/push/subscriptions → {id}).
    /// Pass it as ?current= to /api/push/diagnostics. Null when not registered.
    public Guid? CurrentDeviceId { get; private set; }
    public bool Checking => _inflight is { IsCompleted: false };

    public PushStatus Status =>
        !Secure || Permission == "unsupported" ? PushStatus.Unsupported
        : Permission == "denied" ? PushStatus.Blocked
        : OptedOut ? PushStatus.Off
        : Subscribed && RepairNeeded ? PushStatus.Repairing
        : Subscribed ? PushStatus.On
        : Checking && RepairNeeded ? PushStatus.Repairing
        : LastError is not null ? PushStatus.Error
        : PushStatus.Off;

    public event Action? Changed;

    private string? _publicKey;
    private Task? _inflight;
    private bool _again, _againForce, _againClaim;
    private DateTime _lastRun = DateTime.MinValue;

    // ───────────────────────── public API (unchanged signatures) ─────────────────────────

    /// After sign-in: read state and, when permission is already granted and the user has not
    /// opted out, (re)subscribe silently. Never prompts.
    public async Task StartAsync()
    {
        try
        {
            var cfg = await _http.GetFromJsonAsync<PushConfig>("/api/push/config");
            ServerEnabled = cfg?.Enabled == true;
            _publicKey = cfg?.PublicKey;
        }
        catch { ServerEnabled = false; }
        try
        {
            _self ??= DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("ipopPush.start", _self);
        }
        catch { /* old cached script: no triggers, start-up reconcile still runs */ }
        await HelloAsync();
        await CheckAsync("start", force: true);
        BannerVisible = ServerEnabled && Secure && Permission == "default" && !OptedOut && !await BannerSnoozedAsync();
        Changed?.Invoke();
    }

    /// From a click only: the browser shows its "Allow notifications" prompt.
    public async Task EnableAsync()
    {
        BannerVisible = false;
        Changed?.Invoke();
        try { await _js.InvokeAsync<JsLegacyState>("ipop.enableDesktopAlerts"); } catch { }
        var acct = await AcctAsync();
        await SetIntentAsync(acct, optedOut: false);
        OptedOut = false;
        await CheckAsync("enable", force: true);
    }

    /// Settings → "turn off in this browser". Permission stays granted; only the subscription goes.
    public async Task DisableAsync()
    {
        var acct = await AcctAsync();
        var gen = await SetIntentAsync(acct, optedOut: true);
        OptedOut = true;
        await UnregisterAsync(withServer: true);
        await BindAsync(acct, gen, optedOut: true);
        Changed?.Invoke();
    }

    /// Before sign-out (the call needs the token): this browser stops receiving the account's toasts.
    public async Task SignOutAsync()
    {
        var gen = await SetIntentAsync(null, OptedOut);
        await UnregisterAsync(withServer: true);
        await BindAsync(null, gen, OptedOut);
        BannerVisible = false;
        Changed?.Invoke();
    }

    public async Task DismissBannerAsync()
    {
        BannerVisible = false;
        await SetLocalAsync(BannerKey, DateTime.UtcNow.ToString("O"));
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        await ReadStateAsync();
        await HelloAsync();
        Changed?.Invoke();
    }

    /// Sends the sound handoff to the worker: whether toasts play a sound, and the master volume
    /// (0..1). The volume is read from ipopSound when null. Call it when Settings saves.
    public async Task SyncPreferencesAsync(bool? soundEnabled = null, double? volume = null)
    {
        var enabled = soundEnabled ?? _notifs?.CurrentPlaySound ?? true;
        try { await _js.InvokeVoidAsync("ipopPush.sendPreferences", enabled, volume); } catch { }
    }

    /// Bounded, single-flight reconcile. `force` skips the throttle (start, enable, repair).
    /// Only "start" and "enable" may claim this browser's push for the signed-in account. Other
    /// triggers (focus/online/visible/periodic/repair) stand down when the shared intent says
    /// signed out, opted out or another account, so a stale tab cannot undo a logout elsewhere.
    public Task CheckAsync(string reason = "manual", bool force = false)
    {
        var claim = reason is "start" or "enable";
        // IsCompleted, not null: a run that finishes synchronously completes before it is assigned.
        if (_inflight is { IsCompleted: false })
        {
            if (force || claim) { _again = true; _againForce |= force; _againClaim |= claim; }
            return _inflight;
        }
        if (!force && DateTime.UtcNow - _lastRun < MinCheckInterval) return Task.CompletedTask;
        _inflight = RunAsync(force || reason == "repair", claim);
        return _inflight;
    }

    private async Task RunAsync(bool force, bool claim)
    {
        try
        {
            do
            {
                _again = false;
                _lastRun = DateTime.UtcNow;
                await ReconcileAsync(force, claim);
                force = _againForce; claim = _againClaim;
                _againForce = false; _againClaim = false;
            } while (_again);
        }
        finally
        {
            await PublishHandoffAsync();
            Changed?.Invoke();
        }
    }

    // ───────────────────────── JS → .NET ─────────────────────────

    /// Another tab changed the intent (disable / sign-out / another account). Stand down if it is
    /// no longer ours; never re-claim from here, or two live accounts would ping-pong.
    [JSInvokable]
    public async Task OnPushIntentChanged()
    {
        var intent = await IntentAsync();
        if (intent is null) return;
        OptedOut = intent.OptedOut;
        var acct = await AcctAsync();
        if (intent.OptedOut || intent.Acct != acct)
        {
            Endpoint = null;
            BindingAcked = false;
            await PublishHandoffAsync();
        }
        Changed?.Invoke();
    }

    [JSInvokable]
    public Task OnPushCheck(string reason) => CheckAsync(reason);

    /// sw.js saw pushsubscriptionchange. Re-register through the authenticated page.
    [JSInvokable]
    public Task OnPushRepair()
    {
        RepairNeeded = true;
        Changed?.Invoke();
        return CheckAsync("repair", force: true);
    }

    // ───────────────────────── core ─────────────────────────

    private async Task ReconcileAsync(bool force, bool claim)
    {
        LastCheckedAt = DateTime.UtcNow;
        await ReadStateAsync();
        if (!_auth.IsAuthenticated) return;
        // No service worker or crypto.subtle on plain http, so stop before hashing.
        if (!Secure) { Fail("insecure"); return; }
        if (Permission == "unsupported") { Fail("unsupported"); return; }
        var acct = await AcctAsync();
        if (acct is null) return; // Me not loaded yet; the next trigger retries

        var intent = await IntentAsync() ?? new JsIntent(0, null, false);
        OptedOut = intent.OptedOut;
        var gen = intent.Gen;
        if (intent.OptedOut)
        {
            Endpoint = null;
            if (intent.Acct == acct) await BindAsync(acct, gen, optedOut: true);
            return;
        }
        if (intent.Acct != acct || gen == 0)
        {
            if (!claim) { Endpoint = null; BindingAcked = false; return; }
            gen = await SetIntentAsync(acct, optedOut: false);
        }

        if (Permission == "denied") { Fail("permission_denied"); Endpoint = null; await BindAsync(acct, gen, false); return; }
        if (Permission != "granted") { LastError = null; Endpoint = null; await BindAsync(acct, gen, false); return; }
        if (!ServerEnabled || string.IsNullOrEmpty(_publicKey)) { Fail("server_disabled"); return; }

        JsSubscribeResult? sub;
        try { sub = await _js.InvokeAsync<JsSubscribeResult?>("ipopPush.subscribe", _publicKey); }
        catch { sub = null; }
        if (sub is null || !sub.Ok || sub.Endpoint is null)
        {
            Endpoint = null;
            LastError = PushError.From(sub?.Code ?? "subscribe_failed", sub?.Message);
            return;
        }

        // Lost the race to a disable or sign-out that happened while the browser subscribed.
        if (!await IsCurrentAsync(gen)) { await UndoIfNoLongerOursAsync(acct); return; }

        var digest = await DigestAsync(sub.Endpoint);
        var memo = await MemoAsync();
        var fresh = memo is not null && memo.Acct == acct && memo.Ep == digest
            && DateTimeOffset.FromUnixTimeMilliseconds(memo.At).UtcDateTime > DateTime.UtcNow - ServerRefresh;
        if (force || sub.Created || !fresh || RepairNeeded)
        {
            var (error, id) = await PostSubscriptionAsync(sub);
            if (error is not null) { Endpoint = null; LastError = error; return; }
            CurrentDeviceId = id;
            await RememberAsync(acct, digest, id);
        }
        else CurrentDeviceId = memo!.Id;

        if (!await IsCurrentAsync(gen)) { await UndoIfNoLongerOursAsync(acct); return; }

        Endpoint = sub.Endpoint;
        LastError = null;
        await BindAsync(acct, gen, false);
        if (RepairNeeded)
        {
            RepairNeeded = false;
            try { await _js.InvokeVoidAsync("ipopPush.repairDone"); } catch { }
        }
        await SyncPreferencesAsync();
    }

    private async Task<(PushError? Error, Guid? Id)> PostSubscriptionAsync(JsSubscribeResult sub)
    {
        try
        {
            var res = await _auth.SendAsync(HttpMethod.Post, "/api/push/subscriptions",
                new { sub.Endpoint, sub.P256dh, sub.Auth, ApiBase = _http.BaseAddress?.ToString() });
            if (res.IsSuccessStatusCode)
            {
                Guid? id = null;
                try { id = (await res.Content.ReadFromJsonAsync<SubscribeResponse>())?.Id; } catch { }
                return (null, id);
            }
            return (res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => PushError.From("unauthorized"),
                HttpStatusCode.ServiceUnavailable => PushError.From("server_disabled"),
                _ => PushError.From("server_rejected"),
            }, null);
        }
        catch { return (PushError.From("network"), null); }
    }

    /// A newer intent won. If it says off or another account owns the push here, take back what
    /// this run just did. The newer run has its own token; we still hold ours for the remove.
    private async Task UndoIfNoLongerOursAsync(string acct)
    {
        var now = await IntentAsync();
        Endpoint = null;
        if (now is null || now.OptedOut || now.Acct is null)
            await UnregisterAsync(withServer: true);
        else if (now.Acct != acct)
            await RemoveOnServerAsync(null); // the browser subscription now belongs to the other account
        OptedOut = now?.OptedOut ?? OptedOut;
    }

    private async Task UnregisterAsync(bool withServer)
    {
        string? endpoint = null;
        try { endpoint = await _js.InvokeAsync<string?>("ipopPush.unsubscribe"); } catch { }
        endpoint ??= Endpoint;
        if (withServer) await RemoveOnServerAsync(endpoint);
        Endpoint = null;
        CurrentDeviceId = null;
        BindingAcked = false;
        try { await _js.InvokeVoidAsync("ipopPush.rememberRegistration", null, null, null); } catch { }
        await PublishHandoffAsync();
    }

    private async Task RemoveOnServerAsync(string? endpoint)
    {
        endpoint ??= Endpoint;
        if (endpoint is null || !_auth.IsAuthenticated) return;
        try { await _auth.SendAsync(HttpMethod.Post, "/api/push/subscriptions/remove", new { Endpoint = endpoint }); }
        catch { /* the server forgets it on the next 404/410 */ }
    }

    /// Session wiped (sign-out, refresh refused, password change). The token is gone, so do only
    /// local, unauthenticated cleanup: unbind the worker and drop the browser subscription.
    private void OnSignedOut() => _ = OnSignedOutAsync();

    internal async Task OnSignedOutAsync()
    {
        try
        {
            var intent = await IntentAsync();
            var optedOut = intent?.OptedOut ?? OptedOut;
            // Explicit sign-out has already written a signed-out intent; do not move the generation again.
            var gen = intent is { Acct: null, Gen: > 0 } ? intent.Gen : await SetIntentAsync(null, optedOut);
            await UnregisterAsync(withServer: false);
            await BindAsync(null, gen, optedOut);
        }
        catch { }
        LastError = null;
        RepairNeeded = false;
        BannerVisible = false;
        Changed?.Invoke();
    }

    private void OnPrefsChanged() => _ = SyncPreferencesAsync();

    // ───────────────────────── JS helpers ─────────────────────────

    private async Task HelloAsync()
    {
        try
        {
            var h = await _js.InvokeAsync<JsHello?>("ipopPush.hello");
            SwSchema = h?.Schema ?? 0;
            if (h?.RepairNeeded == true) RepairNeeded = true;
            LastWorkerResult = h?.LastResult is { Outcome: { Length: > 0 } o } r
                ? new PushWorkerResult(o, DateTimeOffset.FromUnixTimeMilliseconds(r.At).UtcDateTime, r.Kind)
                : null;
        }
        catch { SwSchema = 0; }
    }

    private async Task ReadStateAsync()
    {
        try
        {
            var s = await _js.InvokeAsync<JsLegacyState>("ipop.desktopAlertsState");
            Secure = s.Secure;
            Permission = s.Notify;
            Idle = s.Idle;
            if (s.Endpoint is null) Endpoint = null; // the browser lost it: not subscribed, whatever the server says
        }
        catch { Permission = "unsupported"; Endpoint = null; }
    }

    private async Task<string?> AcctAsync()
    {
        var id = _auth.Me?.UserId;
        if (id is null) return null;
        try { return await _js.InvokeAsync<string?>("ipopPush.acct", id.Value.ToString("D")); }
        catch { return null; }
    }

    private async Task<JsIntent?> IntentAsync()
    {
        try { return await _js.InvokeAsync<JsIntent?>("ipopPush.intent"); } catch { return null; }
    }

    private async Task<long> SetIntentAsync(string? acct, bool optedOut)
    {
        try { return await _js.InvokeAsync<long>("ipopPush.setIntent", acct, optedOut); } catch { return 0; }
    }

    private async Task<bool> IsCurrentAsync(long gen)
    {
        try { return await _js.InvokeAsync<bool>("ipopPush.isCurrent", gen); } catch { return true; }
    }

    private async Task BindAsync(string? acct, long gen, bool optedOut)
    {
        try { BindingAcked = await _js.InvokeAsync<bool>("ipopPush.sendBinding", acct, gen, optedOut) && acct is not null && !optedOut; }
        catch { BindingAcked = false; }
    }

    private async Task<string?> DigestAsync(string endpoint)
    {
        try { return await _js.InvokeAsync<string?>("ipopPush.endpointDigest", endpoint); } catch { return null; }
    }

    private async Task<JsMemo?> MemoAsync()
    {
        try { return await _js.InvokeAsync<JsMemo?>("ipopPush.lastRegistration"); } catch { return null; }
    }

    private async Task RememberAsync(string acct, string? digest, Guid? id)
    {
        try { await _js.InvokeVoidAsync("ipopPush.rememberRegistration", acct, digest, id?.ToString("D")); } catch { }
    }

    private async Task PublishHandoffAsync()
    {
        try { await _js.InvokeVoidAsync("ipopPush.setBackgroundHandoff", BackgroundHandoff); } catch { }
    }

    private void Fail(string code) { LastError = PushError.From(code); Endpoint = null; CurrentDeviceId = null; }

    private async Task<bool> BannerSnoozedAsync() =>
        DateTime.TryParse(await GetLocalAsync(BannerKey), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
        && DateTime.UtcNow - at < BannerSnooze;

    private async Task<string?> GetLocalAsync(string key)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", key); } catch { return null; }
    }

    private async Task SetLocalAsync(string key, string? value)
    {
        try
        {
            if (value is null) await _js.InvokeVoidAsync("localStorage.removeItem", key);
            else await _js.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch { }
    }

    public void Dispose()
    {
        _auth.SignedOut -= OnSignedOut;
        if (_notifs is not null) _notifs.PreferencesChanged -= OnPrefsChanged;
        try { _ = _js.InvokeVoidAsync("ipopPush.stop"); } catch { }
        _self?.Dispose();
    }

    private sealed record PushConfig(bool Enabled, string? PublicKey);
    internal sealed record JsLegacyState(bool Secure, string Notify, string Idle, string? Endpoint);
    internal sealed record JsSubscribeResult(bool Ok, string? Code, string? Message, string? Endpoint, string? P256dh, string? Auth, bool Created);
    internal sealed record JsIntent(long Gen, string? Acct, bool OptedOut);
    internal sealed record JsHello(int Schema, bool RepairNeeded, JsWorkerResult? LastResult);
    internal sealed record JsWorkerResult(string? Outcome, long At, string? Kind);
    internal sealed record JsMemo(string? Acct, string? Ep, long At, Guid? Id = null);
    private sealed record SubscribeResponse(Guid? Id);
}

/// Outcome is "notification-created" | "notification-failed" | "suppressed-foreground" (or newer).
public sealed record PushWorkerResult(string Outcome, DateTime AtUtc, string? Kind);

public enum PushStatus { Unsupported, Blocked, Off, Error, Repairing, On }

/// A registration problem, safe to show. Never carries an endpoint, key or token.
public sealed record PushError(string Code, string Message)
{
    private static readonly Dictionary<string, string> Text = new()
    {
        ["unsupported"] = "This browser does not support Web Push.",
        ["insecure"] = "Windows notifications need the secure (https) iPop address.",
        ["permission_denied"] = "Notifications are blocked for iPop in this browser's site settings.",
        ["server_disabled"] = "Push notifications are not configured on the server.",
        ["sw_failed"] = "This phone does not trust the company certificate yet, so notifications cannot start. Install it from ipop.santos.co.id/app (Android: step 1), then reopen iPop.",
        ["subscribe_abort"] = "The browser's push service refused the registration (AbortError). Check that background apps and Google services are allowed, then try again.",
        ["subscribe_not_allowed"] = "The browser did not allow push registration.",
        ["subscribe_invalid_state"] = "The background worker was not ready; it will retry.",
        ["subscribe_not_supported"] = "Push registration is not supported in this browser mode (for example private or kiosk mode).",
        ["subscribe_failed"] = "The browser could not register for push.",
        ["no_key"] = "The server has no push key configured.",
        ["server_rejected"] = "The iPop server did not accept this device.",
        ["unauthorized"] = "Your session could not be verified; sign in again.",
        ["network"] = "Could not reach the iPop server; it will retry.",
    };

    /// The text always comes from this table; a JS message is used only when the code is unknown,
    /// and even then it is trimmed and must not look like a URL.
    public static PushError From(string code, string? jsMessage = null)
    {
        if (Text.TryGetValue(code, out var t)) return new(code, t);
        var safe = jsMessage is { Length: > 0 and <= 200 } m && !m.Contains("://") ? m : Text["subscribe_failed"];
        return new("subscribe_failed", safe);
    }

    public bool Transient => Code is "network" or "subscribe_invalid_state";
}
