using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Windows toasts through Web Push: this browser's permission + subscription, kept in step
/// with /api/push. The service worker (wwwroot/sw.js) shows the toasts; nothing here does.
public class PushState(AuthState auth, HttpClient http, IJSRuntime js)
{
    private const string BannerKey = "ipop-push-banner-dismissed";
    private const string OffKey = "ipop-push-off";
    private static readonly TimeSpan BannerSnooze = TimeSpan.FromDays(7);

    /// "default" | "granted" | "denied" | "unsupported" (plain http, or an old browser).
    public string Permission { get; private set; } = "unsupported";
    public bool Secure { get; private set; }
    /// Push is configured on the server (VAPID keys present).
    public bool ServerEnabled { get; private set; }
    /// This browser has a live subscription registered with the API.
    public bool Subscribed => Endpoint is not null && !_turnedOff;
    public string? Endpoint { get; private set; }
    public string Idle { get; private set; } = "unsupported";
    public bool BannerVisible { get; private set; }

    private bool _turnedOff;
    private string? _publicKey;

    public event Action? Changed;

    /// After sign-in: read state, and — when permission was already granted — (re)subscribe
    /// silently so the device list and ApiBase stay current. Never prompts.
    public async Task StartAsync()
    {
        try
        {
            var cfg = await http.GetFromJsonAsync<PushConfig>("/api/push/config");
            ServerEnabled = cfg?.Enabled == true;
            _publicKey = cfg?.PublicKey;
        }
        catch { ServerEnabled = false; }
        _turnedOff = await GetLocalAsync(OffKey) == "1";
        await ReadStateAsync();
        if (Permission == "granted" && !_turnedOff) await RegisterAsync();
        BannerVisible = ServerEnabled && Secure && Permission == "default" && !await BannerSnoozedAsync();
        Changed?.Invoke();
    }

    /// From a click only: the browser shows its "Allow notifications" prompt.
    public async Task EnableAsync()
    {
        BannerVisible = false;
        Changed?.Invoke();
        try { await js.InvokeAsync<JsState>("ipop.enableDesktopAlerts"); } catch { }
        _turnedOff = false;
        await SetLocalAsync(OffKey, null);
        await ReadStateAsync();
        if (Permission == "granted") await RegisterAsync();
        Changed?.Invoke();
    }

    /// Settings → "matikan di browser ini". Permission stays granted; only the subscription goes.
    public async Task DisableAsync()
    {
        _turnedOff = true;
        await SetLocalAsync(OffKey, "1");
        await UnregisterAsync();
        Changed?.Invoke();
    }

    /// Before sign-out (the call needs the token): this browser stops receiving the account's toasts.
    public async Task SignOutAsync()
    {
        await UnregisterAsync();
        BannerVisible = false;
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
        Changed?.Invoke();
    }

    private async Task ReadStateAsync()
    {
        try
        {
            var s = await js.InvokeAsync<JsState>("ipop.desktopAlertsState");
            Secure = s.Secure;
            Permission = s.Notify;
            Idle = s.Idle;
            Endpoint = s.Endpoint;
        }
        catch { Permission = "unsupported"; Endpoint = null; }
    }

    private async Task RegisterAsync()
    {
        if (!ServerEnabled || string.IsNullOrEmpty(_publicKey)) return;
        try
        {
            var sub = await js.InvokeAsync<JsSubscription?>("ipop.pushSubscribe", _publicKey);
            if (sub is null) return;
            var res = await auth.SendAsync(HttpMethod.Post, "/api/push/subscriptions",
                new { sub.Endpoint, sub.P256dh, sub.Auth, ApiBase = http.BaseAddress?.ToString() });
            if (res.IsSuccessStatusCode) Endpoint = sub.Endpoint;
        }
        catch { /* best-effort: the next sign-in retries */ }
    }

    private async Task UnregisterAsync()
    {
        try
        {
            var endpoint = await js.InvokeAsync<string?>("ipop.pushUnsubscribe") ?? Endpoint;
            if (endpoint is not null && auth.IsAuthenticated)
                await auth.SendAsync(HttpMethod.Post, "/api/push/subscriptions/remove", new { Endpoint = endpoint });
        }
        catch { /* best-effort */ }
        Endpoint = null;
    }

    private async Task<bool> BannerSnoozedAsync() =>
        DateTime.TryParse(await GetLocalAsync(BannerKey), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
        && DateTime.UtcNow - at < BannerSnooze;

    private async Task<string?> GetLocalAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", key); } catch { return null; }
    }

    private async Task SetLocalAsync(string key, string? value)
    {
        try
        {
            if (value is null) await js.InvokeVoidAsync("localStorage.removeItem", key);
            else await js.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch { }
    }

    private sealed record PushConfig(bool Enabled, string? PublicKey);
    private sealed record JsState(bool Secure, string Notify, string Idle, string? Endpoint);
    private sealed record JsSubscription(string Endpoint, string P256dh, string Auth);
}
