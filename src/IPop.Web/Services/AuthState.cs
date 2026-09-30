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

    /// The current access token for Hub connections (ChatState, etc.). Null when signed out.
    public string? AccessToken => _accessToken;
    public string MyStatus { get; private set; } = "off";

    /// Presence-hub link state, for the Directory page's status pill.
    public DirectoryConnectionState ConnectionState { get; private set; } = DirectoryConnectionState.Connecting;
    public event Action? ConnectionStateChanged;
    public event Action<string, string>? PresenceChanged;
    public event Action? DirectoryChanged;
    public event Action? AuditChanged;

    /// Raised after the app hub comes back from a drop. Events pushed while it was down are
    /// gone, so listeners re-seed over REST.
    public event Action? HubReconnected;

    /// Raised once when the refresh token is refused and the session is genuinely over (14-day
    /// expiry, admin revoke, deactivation) — as opposed to the routine 15-minute access-token
    /// renewal. The shell listens and routes to /login; without it the app kept beating against a
    /// dead session behind a red error bar and a permanent "Reconnecting…".
    public event Action? SessionEnded;

    /// The signed-in user's own directory row, loaded lazily by the shell.
    public DirectoryEmployeeDto? Me { get; private set; }

    /// Roles ride along on /api/directory/me — no extra round-trip just to gate the Admin nav.
    public bool IsAdmin => Me?.Roles.Contains("Admin") == true;

    public async Task<DirectoryEmployeeDto?> LoadMeAsync()
        => Me ??= await GetAsync<DirectoryEmployeeDto>("/api/directory/me");

    /// Raised when Me is re-read after a self-service profile edit (email, photo).
    public event Action? MeChanged;

    public async Task ReloadMeAsync()
    {
        Me = await GetAsync<DirectoryEmployeeDto>("/api/directory/me") ?? Me;
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

    /// Starts the real-time presence connection and a 60-second visible-tab heartbeat.
    public async Task StartPresenceAsync()
    {
        if (!IsAuthenticated || _presence is not null) return;
        var hubUrl = new Uri(new Uri(http.BaseAddress!, "/"), "hubs/app");
        _presence = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_accessToken))
            .WithAutomaticReconnect()
            .Build();
        _presence.On<string, string>(RealtimeEvents.PresenceChanged, (nik, status) =>
        {
            if (Me?.Nik == nik) MyStatus = status;
            PresenceChanged?.Invoke(nik, status);
        });
        _presence.On(RealtimeEvents.DirectoryChanged, () => DirectoryChanged?.Invoke());
        _presence.On(RealtimeEvents.AuditChanged, () => AuditChanged?.Invoke());
        foreach (var bind in _subscriptions.Values) bind(_presence);
        _presence.Reconnecting += _ => { SetConnectionState(DirectoryConnectionState.Reconnecting); return Task.CompletedTask; };
        _presence.Reconnected += _ =>
        {
            DirectoryChanged?.Invoke();
            HubReconnected?.Invoke();
            return Task.CompletedTask;
        };
        _presence.Closed += _ => { SetConnectionState(DirectoryConnectionState.Offline); return Task.CompletedTask; };
        try
        {
            await _presence.StartAsync();
            SetConnectionState(DirectoryConnectionState.Live);
        }
        catch { await _presence.DisposeAsync(); _presence = null; SetConnectionState(DirectoryConnectionState.Offline); }
        if (_presence is not null) await BeatWhenVisibleAsync();

        if (_presence is not null)
            _heartbeat = new Timer(_ => _ = BeatWhenVisibleAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public async Task SetBusyAsync(bool busy)
    {
        var res = await PostWithAuth("/api/presence/status", new { status = busy ? "busy" : (string?)null });
        if (!res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadFromJsonAsync<PresenceDto>();
        if (body is not null) { MyStatus = body.Status; PresenceChanged?.Invoke(Me?.Nik ?? "", MyStatus); }
    }

    private async Task BeatWhenVisibleAsync()
    {
        try
        {
            if (await js.InvokeAsync<string>("ipop.visibility") == "visible") await BeatAsync();
        }
        catch { /* presence must never take down the shell */ }
    }

    private async Task BeatAsync()
    {
        var res = await PostWithAuth("/api/presence/beat", new { });
        if (!res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadFromJsonAsync<PresenceDto>();
        if (body is not null) MyStatus = body.Status;
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

    public async Task<(bool Ok, Dictionary<string, string[]>? Fields, string? Error)> ChangePasswordAsync(string newPassword, string confirm)
    {
        var res = await PostWithAuth("/api/auth/change-password", new { newPassword, confirmPassword = confirm });
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

    /// GET with bearer + one silent refresh retry on 401 (spec §3.2).
    public async Task<T?> GetAsync<T>(string url, CancellationToken ct = default)
    {
        var res = await Send(HttpMethod.Get, url, null, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && await TryRestoreAsync())
            res = await Send(HttpMethod.Get, url, null, ct);
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (!res.IsSuccessStatusCode) return default;
        // Guard empty bodies: 204/idle endpoints (e.g. /api/calls/ringing with no ring)
        // return 200 + Content-Length: 0 → ReadFromJsonAsync would throw and crash the page.
        if (res.Content.Headers.ContentLength is 0) return default;
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    public async Task SignOutAsync()
    {
        var refresh = await js.InvokeAsync<string?>("localStorage.getItem", RefreshKey)
            ?? await js.InvokeAsync<string?>("sessionStorage.getItem", RefreshKey);
        if (!string.IsNullOrEmpty(refresh))
            try { await http.PostAsJsonAsync("/api/auth/logout", new { refreshToken = refresh }); } catch { /* best-effort */ }
        await ClearAsync();
    }

    private async Task ClearAsync()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        _bound.Clear();   // handlers die with the connection; _subscriptions re-bind on next start
        if (_presence is not null) await _presence.DisposeAsync();
        _presence = null;
        await js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", RefreshKey);
        _accessToken = null;
        MustChangePassword = false;
        MyStatus = "off";
        Me = null;
        IPop.Web.Models.AvatarPhotos.Clear();
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

    public async Task<HttpResponseMessage> SendMultipartAsync(string url, MultipartFormDataContent content)
    {
        // MultipartFormDataContent's inner streams are consumed on Send — buffer once so 401 retry can resend.
        var buffered = await content.ReadAsByteArrayAsync();
        var contentType = content.Headers.ContentType?.ToString();
        content.Dispose();

        async Task<HttpResponseMessage> SendOnce(string? token)
        {
            var c = new ByteArrayContent(buffered);
            if (contentType is not null) c.Headers.TryAddWithoutValidation("Content-Type", contentType);
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = c };
            if (token is not null) req.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(req);
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
    private record PresenceDto(string Status);
    private record ApiError(string? Code, string? Message, Dictionary<string, string[]>? Fields);
}
