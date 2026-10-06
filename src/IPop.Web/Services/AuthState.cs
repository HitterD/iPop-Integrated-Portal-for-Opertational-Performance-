using System.Net;
using System.Net.Http.Json;
using IPop.Application.Realtime;
using IPop.Web.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Client auth state for Blazor WASM. Access token lives in memory only;
/// Refresh token persists in localStorage when requested, otherwise in sessionStorage.
/// Move to HttpOnly cookies when hosting consolidates to a single origin; browser
/// storage is XSS-readable, accepted for the development phase.
public class AuthState(HttpClient http, IJSRuntime js)
{
    private const string RefreshKey = "ipop.refresh";
    private string? _accessToken;
    private bool _keepSignedIn = true;
    private HubConnection? _presence;
    private Timer? _heartbeat;

    /// Serializes token refresh; _refreshGeneration lets a caller that queued behind a successful
    /// refresh return that result instead of spending the rotated token again. See TryRestoreAsync.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshGeneration;

    public bool MustChangePassword { get; private set; }
    public bool IsAuthenticated => _accessToken is not null;

    /// Another tab of this browser owns the session (SessionLockState): realtime stays off and
    /// the hub retry loops stand down until this tab takes it back.
    public bool IsSuspended { get; set; }

    /// The current access token for Hub connections (ChatState, etc.). Null when signed out.
    public string? AccessToken => _accessToken;
    public string MyStatus { get; internal set; } = "off";
    /// When a timed Busy ends (UTC); null = until turned off, or not Busy.
    public DateTime? BusyUntil { get; private set; }
    /// Busy mutes banners, sounds and call ringing (calls still show). Only while online.
    public bool IsBusy => MyStatus == "busy";

    /// Presence-hub link state, for the Directory page's status pill.
    public DirectoryConnectionState ConnectionState { get; private set; } = DirectoryConnectionState.Connecting;
    public event Action? ConnectionStateChanged;
    public event Action<string, string>? PresenceChanged;
    public event Action? DirectoryChanged;
    public event Action? AuditChanged;

    /// Raised after the app hub comes back from a drop (auto-reconnect or a Closed retry).
    /// Events pushed while it was down are gone, so listeners re-seed over REST.
    public event Action? HubReconnected;

    /// Raised once when the refresh token is refused and the session is genuinely over (14-day
    /// expiry, admin revoke, deactivation) — as opposed to the routine 15-minute access-token
    /// renewal. The shell listens and routes to /login; without it the app kept beating against a
    /// dead session behind a red error bar and a permanent "Reconnecting…".
    public event Action? SessionEnded;

    /// Raised whenever local session state is wiped (sign-out, session end, password change),
    /// so per-user caches can drop what they hold.
    public event Action? SignedOut;

    /// The signed-in user's own directory row, loaded lazily by the shell.
    public DirectoryEmployeeDto? Me { get; private set; }

    /// Roles ride along on /api/directory/me — no extra round-trip just to gate the Admin nav.
    public bool IsAdmin => Me?.Roles.Contains("Admin") == true;

    public async Task<DirectoryEmployeeDto?> LoadMeAsync()
    {
        if (Me is not null) return Me;
        Me = await GetAsync<DirectoryEmployeeDto>("/api/directory/me");
        await ShareUserAsync();
        return Me;
    }

    /// Scripts that keep their own per-account settings (sounds.js) read the NIK from ipopUserStore.
    private async Task ShareUserAsync()
    {
        try { await js.InvokeVoidAsync("ipopUserStore.setUser", Me?.Nik); } catch { /* not loaded (tests) */ }
    }

    /// localStorage scoped to the signed-in account (js/user-store.js). A value saved before
    /// per-account keys existed is handed to the first account that reads it, then removed.
    public async Task<string?> GetUserSettingAsync(string key)
    {
        if (Me?.Nik is not { Length: > 0 } nik) return null;
        try { return await js.InvokeAsync<string?>("ipopUserStore.get", nik, key); } catch { return null; }
    }

    public async Task RemoveUserSettingAsync(string key)
    {
        if (Me?.Nik is not { Length: > 0 } nik) return;
        try { await js.InvokeVoidAsync("ipopUserStore.remove", nik, key); } catch { }
    }

    public async Task SetUserSettingAsync(string key, string value)
    {
        if (Me?.Nik is not { Length: > 0 } nik) return;
        try { await js.InvokeVoidAsync("ipopUserStore.set", nik, key, value); } catch { }
    }

    /// Raised when Me is re-read after a self-service profile edit (email, photo).
    public event Action? MeChanged;

    public async Task ReloadMeAsync()
    {
        Me = await GetAsync<DirectoryEmployeeDto>("/api/directory/me") ?? Me;
        await ShareUserAsync();
        MeChanged?.Invoke();
    }

    /// Reloads the NIK → photo map every avatar renders from.
    public async Task LoadPhotosAsync()
        => IPop.Web.Models.AvatarPhotos.Set(http.BaseAddress!, await GetAsync<Dictionary<string, string>>("/api/photos"));

    /// Bearer + one silent-refresh retry on 401 — the public form of PostWithAuth for pages
    /// that PATCH/PUT/POST, so Admin never has to reach past Send itself.
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, CancellationToken ct = default)
    {
        var res = await Send(method, url, body, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && await TryRestoreAsync())
            res = await Send(method, url, body, ct);
        return res;
    }

    private readonly Dictionary<Guid, Func<HubConnection, IDisposable>> _subscriptions = [];
    private readonly Dictionary<Guid, IDisposable> _bound = [];

    /// Subscribe to a hub event without editing AuthState. Survives sign-out/in (re-bound on
    /// each new connection); dispose the result to unsubscribe.
    public IDisposable On<T>(string evt, Action<T> handler) => Subscribe(c => c.On(evt, handler));
    public IDisposable On<T1, T2>(string evt, Action<T1, T2> handler) => Subscribe(c => c.On(evt, handler));
    public IDisposable On(string evt, Action handler) => Subscribe(c => c.On(evt, handler));

    private IDisposable Subscribe(Func<HubConnection, IDisposable> bind)
    {
        var id = Guid.NewGuid();
        _subscriptions[id] = c => _bound[id] = bind(c);
        if (_presence is not null) _subscriptions[id](_presence);
        return new Unsubscriber(() =>
        {
            _subscriptions.Remove(id);
            if (_bound.Remove(id, out var b)) b.Dispose();
        });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// Starts the real-time presence connection, idle detection, and a 30-second heartbeat.
    public async Task StartPresenceAsync()
    {
        if (!IsAuthenticated || IsSuspended || _presence is not null) return;
        var hubUrl = new Uri(new Uri(http.BaseAddress!, "/"), "hubs/app");
        _presence = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_accessToken))
            .WithAutomaticReconnect()
            .Build();
        _presence.On<string, string>(RealtimeEvents.PresenceChanged, (nik, status) =>
        {
            if (Me?.Nik == nik) { MyStatus = status; if (status != "busy") BusyUntil = null; }
            PresenceChanged?.Invoke(nik, status);
        });
        _presence.On(RealtimeEvents.DirectoryChanged, () => DirectoryChanged?.Invoke());
        _presence.On(RealtimeEvents.AuditChanged, () => AuditChanged?.Invoke());
        foreach (var bind in _subscriptions.Values) bind(_presence);
        _presence.Reconnecting += _ => { SetConnectionState(DirectoryConnectionState.Reconnecting); return Task.CompletedTask; };
        _presence.Reconnected += _ => { OnHubBack(); return Task.CompletedTask; };
        var conn = _presence;
        _presence.Closed += ex =>
        {
            SetConnectionState(DirectoryConnectionState.Offline);
            _ = HubRetry.RunAsync(conn, () => _presence == conn && IsAuthenticated && !IsSuspended, OnHubBack);
            return Task.CompletedTask;
        };
        try
        {
            await _presence.StartAsync();
            SetConnectionState(DirectoryConnectionState.Live);
        }
        catch
        {
            SetConnectionState(DirectoryConnectionState.Offline);
            _ = HubRetry.RunAsync(conn, () => _presence == conn && IsAuthenticated && !IsSuspended, OnHubBack);
        }
        // HTTP heartbeats and idle detection must keep working even when SignalR cannot connect.

        try
        {
            _selfRef ??= DotNetObjectReference.Create(this);
            await js.InvokeVoidAsync("ipop.presenceStart", _selfRef);
        }
        catch { /* idle detection is best-effort; beats still flow */ }
        await SafeBeatAsync();
        _heartbeat = new Timer(_ => _ = SafeBeatAsync(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// Back online after a drop: presence pushed meanwhile was missed, so beat now (our own
    /// status reads "on" again for everyone) and have listeners re-fetch.
    private void OnHubBack()
    {
        SetConnectionState(DirectoryConnectionState.Live);
        _ = SafeBeatAsync();
        DirectoryChanged?.Invoke();
        HubReconnected?.Invoke();
    }

    /// Every open tab beats, visible or not: an open iPop tab means the user is at their
    /// computer. Hidden tabs are throttled by the browser to ~1/min, which the server's
    /// two-minute offline threshold tolerates.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private DotNetObjectReference<AuthState>? _selfRef;
    private bool _idle;
    /// One lease per tab: Away needs every open tab/device idle, and logout drops only this one.
    private readonly string _deviceId = Guid.NewGuid().ToString("N");

    /// Called from ipop.js when the computer goes idle/locked (away) or becomes active again.
    [JSInvokable]
    public Task OnIdleChanged(bool idle)
    {
        _idle = idle;
        return SafeBeatAsync();
    }

    /// duration: "1h", "2h", "today", or null for until turned off. Ignored when clearing Busy.
    public async Task SetBusyAsync(bool busy, string? duration = null)
    {
        var res = await PostWithAuth("/api/presence/status",
            new { status = busy ? "busy" : (string?)null, duration = busy ? duration : null });
        if (!res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadFromJsonAsync<PresenceDto>();
        if (body is not null) { MyStatus = body.Status; BusyUntil = body.BusyUntil; PresenceChanged?.Invoke(Me?.Nik ?? "", MyStatus); }
    }

    private async Task SafeBeatAsync()
    {
        try { await BeatAsync(); }
        catch { /* presence must never take down the shell */ }
    }

    private async Task BeatAsync()
    {
        if (!IsAuthenticated) return;
        var res = await PostWithAuth("/api/presence/beat", new { idle = _idle, device = _deviceId });
        if (!res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadFromJsonAsync<PresenceDto>();
        if (body is not null) { MyStatus = body.Status; BusyUntil = body.BusyUntil; }
    }

    public async Task<(bool Ok, string? Error, bool Locked)> LoginAsync(string nik, string password, string? fingerprint, bool keepSignedIn = true)
    {
        var res = await http.PostAsJsonAsync("/api/auth/login", new { nik, password, deviceFingerprint = fingerprint });
        if (!res.IsSuccessStatusCode)
        {
            var err = await ReadError(res);
            return (false, err?.Message ?? "Sign-in failed.", err?.Code == "locked");
        }
        var body = (await res.Content.ReadFromJsonAsync<LoginDto>())!;
        _accessToken = body.AccessToken;
        MustChangePassword = body.MustChangePassword;
        _keepSignedIn = keepSignedIn;
        await js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", RefreshKey);
        await js.InvokeVoidAsync(keepSignedIn ? "localStorage.setItem" : "sessionStorage.setItem", RefreshKey, body.RefreshToken);
        if (!MustChangePassword) await StartPresenceAsync();
        return (true, null, false);
    }

    public async Task<(bool Ok, Dictionary<string, string[]>? Fields, string? Error)> ChangePasswordAsync(string newPassword, string confirm, string? currentPassword = null)
    {
        var res = await PostWithAuth("/api/auth/change-password", new { newPassword, confirmPassword = confirm, currentPassword });
        if (!res.IsSuccessStatusCode)
        {
            var err = await ReadError(res);
            return (false, err?.Fields, err?.Message ?? "Could not update the password.");
        }
        // Global invalidation server-side killed all sessions — drop ours too.
        await ClearAsync();
        return (true, null, null);
    }

    /// Anonymous. The server answers 202 whether or not the account exists; only a
    /// rate-limit (429) or transport failure is worth telling the user about.
    public async Task<(bool Ok, string? Error)> RequestPasswordResetAsync(string identifier)
    {
        var res = await http.PostAsJsonAsync("/api/auth/forgot-password", new { identifier });
        if (res.IsSuccessStatusCode) return (true, null);
        return (false, (int)res.StatusCode == 429
            ? "Too many requests. Wait a few minutes and try again."
            : "Could not send the reset email. Try again later.");
    }

    public async Task<(bool Ok, Dictionary<string, string[]>? Fields, string? Error)> ResetPasswordAsync(string token, string newPassword, string confirm)
    {
        var res = await http.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword, confirmPassword = confirm });
        if (res.IsSuccessStatusCode) return (true, null, null);
        if ((int)res.StatusCode == 429) return (false, null, "Too many attempts. Wait a few minutes and try again.");
        var err = await ReadError(res);
        return (false, err?.Fields, err?.Message ?? "Could not reset the password.");
    }

    /// Restore session from the stored refresh token on app start. False → go to /login.
    ///
    /// Single-flight, and it has to be. The server ROTATES refresh tokens: a successful refresh
    /// revokes the token it was handed (AuthWorkflow.RefreshAsync), so replaying a spent one comes
    /// back 401 — indistinguishable from "your session is over". When the 15-minute access token
    /// expires, every request in flight 401s at the same instant (Dashboard alone fans out seven),
    /// and without this gate each one refreshed in parallel: one won, the rest presented the token
    /// it had just revoked, and every one of those failures deleted the good refresh token the
    /// winner had only just stored. The session destroyed itself every quarter of an hour.
    public async Task<bool> TryRestoreAsync()
    {
        // Sampled BEFORE queueing: if it moves while we wait, another caller's refresh already
        // covered us, and re-sending our now-stale copy is precisely the replay to avoid.
        var seen = Volatile.Read(ref _refreshGeneration);
        bool ok;
        await _refreshGate.WaitAsync();
        try
        {
            // Generation moved while we queued: another caller already refreshed, and _accessToken
            // is theirs. Report their result rather than spending the token they just rotated away.
            if (Volatile.Read(ref _refreshGeneration) != seen) return _accessToken is not null;
            ok = await RefreshCoreAsync();
            if (ok) Interlocked.Increment(ref _refreshGeneration);
        }
        finally { _refreshGate.Release(); }

        // Deliberately outside the gate: StartPresenceAsync sends the first heartbeat, and a
        // heartbeat that 401s re-enters TryRestoreAsync. On WASM's single thread, doing that while
        // still holding the semaphore is a deadlock the app never wakes up from.
        if (ok && !MustChangePassword) await StartPresenceAsync();
        return ok;
    }

    private async Task<bool> RefreshCoreAsync()
    {
        var refresh = await js.InvokeAsync<string?>("localStorage.getItem", RefreshKey);
        _keepSignedIn = !string.IsNullOrEmpty(refresh);
        if (string.IsNullOrEmpty(refresh)) refresh = await js.InvokeAsync<string?>("sessionStorage.getItem", RefreshKey);
        if (string.IsNullOrEmpty(refresh)) return false;

        HttpResponseMessage res;
        try { res = await http.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }); }
        catch { return false; } // API down — stay signed out rather than crash the router
        if (!res.IsSuccessStatusCode)
        {
            // The server refused the refresh token itself: this session is over and no amount of
            // retrying brings it back. Tear down the timer and the hub, then tell the shell — the
            // heartbeat used to keep firing against a corpse, which is what put the red error bar
            // on screen and left "Reconnecting…" up forever.
            await ClearAsync();
            SessionEnded?.Invoke();
            return false;
        }
        var body = (await res.Content.ReadFromJsonAsync<RefreshDto>())!;
        _accessToken = body.AccessToken;
        MustChangePassword = body.MustChangePassword;
        await js.InvokeVoidAsync(_keepSignedIn ? "localStorage.setItem" : "sessionStorage.setItem", RefreshKey, body.RefreshToken);
        return true; // presence is started by TryRestoreAsync, once the refresh gate is released
    }

    /// Raised when a read could not reach the API at all: timed out after ResilientHandler's
    /// retries, network down, or the proxy answered 502/503/504. The shell turns it into a
    /// visible "couldn't load · Try again" instead of a page that silently stays empty.
    public event Action<string>? RequestFailed;

    /// GET with bearer + one silent refresh retry on 401 (spec §3.2). A transport failure
    /// returns default (as a non-success status always has) and raises RequestFailed.
    public async Task<T?> GetAsync<T>(string url, CancellationToken ct = default)
    {
        // tolerateBadBody: false keeps the old contract: a 2xx with a body that is not the
        // expected JSON still throws to the caller, exactly as before GetResultAsync existed.
        var r = await GetCoreAsync<T>(url, tolerateBadBody: false, ct);
        if (r.Status == GetStatus.Unreachable)
        {
            RequestFailed?.Invoke(url);
            return LastGood<T>(url);
        }
        if (r.Status != GetStatus.Ok) return default;
        Remember(url, r.Value);
        return r.Value;
    }

    /// GET that says what happened instead of collapsing every failure into null. No last-good
    /// fallback and no RequestFailed: the caller owns the error UI and must be able to tell
    /// "not a member / gone" (403/404) from "try again" (network, timeout, 5xx). Caller
    /// cancellation still throws OperationCanceledException. A 2xx whose body is not the
    /// expected JSON (proxy HTML page, truncated body) is Failed, never an exception.
    public Task<GetResult<T>> GetResultAsync<T>(string url, CancellationToken ct = default)
        => GetCoreAsync<T>(url, tolerateBadBody: true, ct);

    private async Task<GetResult<T>> GetCoreAsync<T>(string url, bool tolerateBadBody, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await Send(HttpMethod.Get, url, null, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && await TryRestoreAsync())
                res = await Send(HttpMethod.Get, url, null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TimeoutException or HttpRequestException or TaskCanceledException)
        {
            return new(GetStatus.Unreachable, default, null);
        }
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        var code = res.StatusCode;
        var status = code switch
        {
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => GetStatus.Unreachable,
            HttpStatusCode.Unauthorized => GetStatus.Unauthorized,
            HttpStatusCode.Forbidden => GetStatus.Forbidden,
            HttpStatusCode.NotFound => GetStatus.NotFound,
            _ when !res.IsSuccessStatusCode => GetStatus.Failed,
            // Guard empty bodies: 204/idle endpoints (e.g. /api/calls/ringing with no ring)
            // return 200 + Content-Length: 0 → ReadFromJsonAsync would throw and crash the page.
            _ when res.Content.Headers.ContentLength is 0 => GetStatus.Empty,
            _ => GetStatus.Ok,
        };
        if (status != GetStatus.Ok) return new(status, default, code);
        T? value;
        try { value = await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct); }
        catch (Exception ex) when (tolerateBadBody && !ct.IsCancellationRequested
            && ex is System.Text.Json.JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            return new(GetStatus.Failed, default, code);
        }
        return value is null ? new(GetStatus.Empty, default, code) : new(GetStatus.Ok, value, code);
    }

    // ── Last good answer per URL, this tab only ──
    // When a read cannot reach the API, the page gets what it was shown last time instead of
    // nothing, so a hiccup doesn't blank lists that were fine a moment ago. Memory only (never
    // storage), bounded, and dropped on sign-out with everything else.
    private const int LastGoodLimit = 150;
    private readonly Dictionary<string, object> _lastGood = [];
    private readonly LinkedList<string> _lastGoodOrder = new();

    // Browser WASM is single-threaded, but server-side callers (and tests) run GETs in parallel.
    private readonly Lock _lastGoodLock = new();

    private void Remember<T>(string url, T? value)
    {
        if (value is null) return;
        var key = $"{typeof(T).FullName}|{url}";
        lock (_lastGoodLock)
        {
            if (_lastGood.ContainsKey(key)) _lastGoodOrder.Remove(key);
            _lastGood[key] = value;
            _lastGoodOrder.AddFirst(key);
            while (_lastGoodOrder.Count > LastGoodLimit)
            {
                _lastGood.Remove(_lastGoodOrder.Last!.Value);
                _lastGoodOrder.RemoveLast();
            }
        }
    }

    private T? LastGood<T>(string url)
    {
        lock (_lastGoodLock)
            return _lastGood.TryGetValue($"{typeof(T).FullName}|{url}", out var v) && v is T t ? t : default;
    }

    /// Session moved to another tab: drop the app hub, idle detection and the heartbeat, but keep
    /// the tokens so "Gunakan di sini" can reconnect without signing in again.
    public async Task StopPresenceAsync()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        try { await js.InvokeVoidAsync("ipop.presenceStop"); } catch { }
        _bound.Clear();
        var conn = _presence;
        _presence = null;
        if (conn is not null) await conn.DisposeAsync();
        SetConnectionState(DirectoryConnectionState.Offline);
    }

    public async Task SignOutAsync()
    {
        // While the token is still valid: show offline now instead of after the 2-minute timeout.
        if (IsAuthenticated)
            try { await PostWithAuth("/api/presence/leave", new { device = _deviceId }); } catch { /* best-effort */ }
        var refresh = await js.InvokeAsync<string?>("localStorage.getItem", RefreshKey)
            ?? await js.InvokeAsync<string?>("sessionStorage.getItem", RefreshKey);
        if (!string.IsNullOrEmpty(refresh))
            try { await http.PostAsJsonAsync("/api/auth/logout", new { refreshToken = refresh }); } catch { /* best-effort */ }
        await ClearAsync();
    }

    private async Task ClearAsync()
    {
        // Everything synchronous first: an in-flight request on the old session can end it
        // (SessionEnded → full reload) at any await below, and the page caches and per-user
        // state must already be gone by then.
        _heartbeat?.Dispose();
        _heartbeat = null;
        _idle = false;
        _accessToken = null;
        MustChangePassword = false;
        MyStatus = "off";
        Me = null;
        IsSuspended = false;
        IPop.Web.Models.AvatarPhotos.Clear();
        lock (_lastGoodLock)
        {
            _lastGood.Clear();
            _lastGoodOrder.Clear();
        }
        // Stored page snapshots (PageCache): wiped here too, because PageCache only hears
        // SignedOut once a page has created it in this document (e.g. not after a reload on /chat).
        if (js is IJSInProcessRuntime sync)
        {
            try { sync.InvokeVoid("ipop.cacheClear", PageCache.StoragePrefix); } catch { }
            // Mail inline images fetched with this account's token (blob: URLs) go with it.
            try { sync.InvokeVoid("ipop.mailImages.clear"); } catch { }
        }
        // One by one: a subscriber that throws must not stop the others from dropping this
        // account's data (an exception used to skip every handler after it).
        foreach (var handler in SignedOut?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { Console.Error.WriteLine($"[auth] sign-out cleanup failed: {ex.Message}"); }
        }

        try { await js.InvokeVoidAsync("ipop.presenceStop"); } catch { }
        _bound.Clear();   // handlers die with the connection; _subscriptions re-bind on next start
        var presence = _presence;
        _presence = null;
        if (presence is not null) await presence.DisposeAsync();
        await js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", RefreshKey);
        await ShareUserAsync();
        SetConnectionState(DirectoryConnectionState.Connecting);
    }

    private void SetConnectionState(DirectoryConnectionState state)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        ConnectionStateChanged?.Invoke();
    }

    private async Task<HttpResponseMessage> PostWithAuth(string url, object body)
    {
        var res = await Send(HttpMethod.Post, url, body);
        if (res.StatusCode == HttpStatusCode.Unauthorized && await TryRestoreAsync())
            res = await Send(HttpMethod.Post, url, body);
        return res;
    }

    public async Task<HttpResponseMessage> SendMultipartAsync(string url, MultipartFormDataContent content, CancellationToken ct = default)
    {
        // MultipartFormDataContent's inner streams are consumed on Send — buffer once so 401 retry can resend.
        var buffered = await content.ReadAsByteArrayAsync(ct);
        var contentType = content.Headers.ContentType?.ToString();
        content.Dispose();

        async Task<HttpResponseMessage> SendOnce(string? token)
        {
            var c = new ByteArrayContent(buffered);
            if (contentType is not null) c.Headers.TryAddWithoutValidation("Content-Type", contentType);
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = c };
            if (token is not null) req.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(req, ct);
        }

        var res = await SendOnce(_accessToken);
        if (res.StatusCode == HttpStatusCode.Unauthorized && await TryRestoreAsync())
        {
            res.Dispose();
            res = await SendOnce(_accessToken);
        }
        return res;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (_accessToken is not null) req.Headers.Authorization = new("Bearer", _accessToken);
        return await http.SendAsync(req, ct);
    }

    /// The API returns AuthError as JSON, but a 500/proxy failure returns HTML — don't let that throw.
    private static async Task<ApiError?> ReadError(HttpResponseMessage res)
    {
        try { return await res.Content.ReadFromJsonAsync<ApiError>(); }
        catch { return null; }
    }

    private record LoginDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword);
    private record RefreshDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword);
    private record PresenceDto(string Status, DateTime? BusyUntil = null);
    private record ApiError(string? Code, string? Message, Dictionary<string, string[]>? Fields);
}

/// Outcome of AuthState.GetResultAsync.
public enum GetStatus
{
    /// 2xx with a body.
    Ok,
    /// 2xx with no body (204 / Content-Length 0 / JSON null).
    Empty,
    /// 401 that a token refresh could not fix.
    Unauthorized,
    Forbidden,
    NotFound,
    /// Any other non-success status (400, 409, 500…).
    Failed,
    /// Never answered: network error, timeout, or the proxy said 502/503/504.
    Unreachable,
}

public readonly record struct GetResult<T>(GetStatus Status, T? Value, HttpStatusCode? Code)
{
    public bool IsOk => Status == GetStatus.Ok;
    /// The server says this caller may not (or can no longer) see the resource.
    public bool IsGone => Status is GetStatus.Forbidden or GetStatus.NotFound;
}
