using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IPop.Notifier.Core;

/// Talks to the iPop API as the signed-in person. Access tokens live 15 minutes in memory; the refresh token
/// (saved encrypted by <see cref="SessionStore"/>) renews them and slides the 90-day session. A 401 renews once
/// and retries; a refresh that the server rejects (revoked, password changed, 90 days unused) signs the app out.
public sealed class IPopClient
{
    private readonly SessionStore store;
    private readonly HttpClient http;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? access;
    private DateTime accessExpires;

    public SessionStore.Session? Session { get; private set; }
    public long Generation { get; private set; }
    public Func<Task>? BeforeSessionChanged { get; set; }
    public Func<string, string?, Task<bool>>? ConfirmAccountSwitch { get; set; }
    public event Action? SignedOut;
    private readonly Dictionary<string, (string Verifier, DateTime At)> pendingLinks = new();

    private static string Secret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string PkceChallenge(string verifier) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public string? PrepareBrowserLink(string nonce)
    {
        if (!ValidSecret(nonce)) return null;
        lock (pendingLinks)
        {
            foreach (var expired in pendingLinks.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromSeconds(60)).Select(p => p.Key).ToList()) pendingLinks.Remove(expired);
            if (pendingLinks.Count >= 5) return null;
            var id = Secret(); var verifier = Secret();
            pendingLinks[id] = (verifier, DateTime.UtcNow);
            // The callback origin is configured by the installed app/server, never supplied by the protocol link.
            return WebUrl("/dashboard?notifierNonce=" + nonce + "&notifierRequest=" + id + "&notifierChallenge=" + PkceChallenge(verifier) + "&notifierScheme=" + AppInfo.UrlScheme);
        }
    }
    private static bool ValidSecret(string? s) => s is { Length: 43 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IPopClient(SessionStore store, HttpMessageHandler? handler = null)
    {
        this.store = store;
        http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("iPopNotifier/" + AppInfo.Version);
        Session = store.Load();
    }

    public bool IsSignedIn => Session is not null;
    public Uri Server => new(Session?.Server ?? AppInfo.DefaultServer);
    /// Where links open in the browser: the server's public web address (Notifier:WebBase, e.g.
    /// https://ipop.santos.co.id/) when it gives one, otherwise the address the app signed in to.
    public Uri? WebBaseOverride { get; set; }
    public Uri Web => WebBaseOverride ?? Server;
    public string WebUrl(string? path) => new Uri(Web, (path ?? "/").TrimStart('/')).ToString();

    public sealed record LoginResult(bool Ok, string? Error, bool MustChangePassword);
    private sealed record LoginDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword);
    private sealed record ErrorDto(string? Code, string? Message);

    public async Task<LoginResult> SignInAsync(string server, string nik, string password, CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try { return await SignInCoreAsync(server, nik, password, ct); }
        finally { refreshLock.Release(); }
    }

    private async Task<LoginResult> SignInCoreAsync(string server, string nik, string password, CancellationToken ct)
    {
        var baseUri = new Uri(server.TrimEnd('/') + "/");
        var res = await http.PostAsJsonAsync(new Uri(baseUri, "api/auth/login"), new { nik, password, deviceFingerprint = "notifier:" + Environment.MachineName }, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await SafeRead<ErrorDto>(res, ct);
            return new(false, err?.Message ?? (res.StatusCode == HttpStatusCode.Unauthorized ? "Wrong employee ID or password." : $"iPop answered {(int)res.StatusCode}."), false);
        }
        var dto = (await res.Content.ReadFromJsonAsync<LoginDto>(Json, ct))!;
        if (dto.MustChangePassword) return new(false, "Set a new password in iPop first (open iPop in the browser), then sign in here.", true);
        await ReplaceSessionAsync(new SessionStore.Session(baseUri.ToString(), dto.RefreshToken, store.DeviceKey(), nik, null), dto.AccessToken, dto.AccessTokenExpiresAt, ct);
        return new(true, null, false);
    }

    private sealed record LinkDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, bool MustChangePassword, string Nik, string? Name, bool Switched);
    public sealed record LinkResult(bool Ok, string? Error, string? Name, bool Switched);

    /// ipop-notifier://signin?code=… from the browser: trade the one-time code for this app's own session.
    /// Signed in as someone else, the server only allows it straight after a sign-in in the browser on this PC.
    public async Task<LinkResult> SignInWithCodeAsync(string server, string code, string requestId, CancellationToken ct)
    {
        string verifier;
        lock (pendingLinks)
        {
            if (!pendingLinks.Remove(requestId, out var pending) || DateTime.UtcNow - pending.At > TimeSpan.FromSeconds(60))
                return new(false, "This sign-in wasn't started on this PC. Choose Sign in with browser and try again.", null, false);
            verifier = pending.Verifier;
        }
        await refreshLock.WaitAsync(ct);
        try { return await RedeemCoreAsync(server, code, requestId, verifier, ct); }
        finally { refreshLock.Release(); }
    }

    private async Task<LinkResult> RedeemCoreAsync(string server, string code, string requestId, string verifier, CancellationToken ct)
    {
        var baseUri = new Uri(server.TrimEnd('/') + "/");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/notifier/link/redeem"))
        {
            Content = JsonContent.Create(new { code, deviceKey = store.DeviceKey(), verifier, requestId }, options: Json),
        };
        req.Headers.TryAddWithoutValidation("X-Notifier-Machine", Environment.MachineName);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await SafeRead<ErrorDto>(res, ct);
            return new(false, err?.Message ?? $"iPop answered {(int)res.StatusCode}.", null, false);
        }
        var dto = (await res.Content.ReadFromJsonAsync<LinkDto>(Json, ct))!;
        var switching = Session is not null && (!string.Equals(Session.Nik, dto.Nik, StringComparison.Ordinal) || Session.Server != baseUri.ToString());
        if (dto.MustChangePassword || (switching && (ConfirmAccountSwitch is null || !await ConfirmAccountSwitch(dto.Nik, dto.Name))))
        {
            await RevokeAsync(new SessionStore.Session(baseUri.ToString(), dto.RefreshToken, store.DeviceKey(), dto.Nik, dto.Name));
            return new(false, dto.MustChangePassword ? "Set a new password in iPop first, then try again." : "Account switch cancelled.", null, false);
        }
        await ReplaceSessionAsync(new SessionStore.Session(baseUri.ToString(), dto.RefreshToken, store.DeviceKey(), dto.Nik, dto.Name), dto.AccessToken, dto.AccessTokenExpiresAt, ct);
        return new(true, null, dto.Name, switching);
    }

    /// Signed out: "an app is waiting here", so iPop in the browser on this PC knows to send the sign-in link.
    public async Task SayWaitingAsync(CancellationToken ct)
    {
        try
        {
            using var _ = await http.PostAsJsonAsync(new Uri(Server, "api/notifier/link/waiting"), new { deviceKey = store.DeviceKey() }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    private async Task RevokeAsync(SessionStore.Session s)
    {
        try { using var _ = await http.PostAsJsonAsync(new Uri(new Uri(s.Server), "api/auth/logout"), new { refreshToken = s.RefreshToken }); } catch { }
    }

    // Called under the auth gate. Teardown runs with the OLD token so remote sessions can be ended correctly.
    private bool transitioning;
    private async Task RetireAsync()
    {
        transitioning = true;
        Generation++;
        if (BeforeSessionChanged is not null) await BeforeSessionChanged();
        if (Session is { } old && access is not null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(old.Server), "api/notifier/devices/offline"));
                req.Headers.Authorization = new("Bearer", access);
                req.Content = JsonContent.Create(new { deviceKey = old.DeviceKey });
                using var _ = await http.SendAsync(req);
            }
            catch { }
        }
    }

    private async Task ReplaceSessionAsync(SessionStore.Session next, string token, DateTime expires, CancellationToken ct)
    {
        var old = Session;
        try
        {
            await RetireAsync();
            store.Save(next);
            Session = next; access = token; accessExpires = expires.ToUniversalTime(); WebBaseOverride = null;
        }
        catch
        {
            Session = null; access = null; store.Clear();
            await RevokeAsync(next);
            if (old is not null) await RevokeAsync(old);
            SignedOut?.Invoke();
            throw;
        }
        finally { transitioning = false; }
        // Signed-in state supersedes the anonymous waiting heartbeat, including password sign-in.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(next.Server), "api/notifier/link/clear"));
            req.Headers.Authorization = new("Bearer", token);
            req.Content = JsonContent.Create(new { deviceKey = next.DeviceKey });
            using var _ = await http.SendAsync(req, ct);
        }
        catch { }
        if (old is not null) await RevokeAsync(old);
    }

    public async Task SignOutAsync()
    {
        await refreshLock.WaitAsync();
        try { await SignOutCoreAsync(); }
        finally { refreshLock.Release(); }
    }
    private async Task SignOutCoreAsync()
    {
        var s = Session;
        try { await RetireAsync(); }
        finally
        {
            Session = null; access = null; WebBaseOverride = null; transitioning = false;
            lock (pendingLinks) pendingLinks.Clear();
            store.Clear();
            if (s is not null) await RevokeAsync(s);
            SignedOut?.Invoke();
        }
    }

    // Teardown only: use a captured old token without re-entering the auth gate.
    public async Task EndRemoteForTransitionAsync(Guid id)
    {
        if (Session is null || access is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, $"api/notifier/remote/sessions/{id}/end"));
            req.Headers.Authorization = new("Bearer", access);
            req.Content = JsonContent.Create(new { files = "" });
            using var _ = await http.SendAsync(req);
        }
        catch { }
    }

    /// A valid access token (renews when it expires within a minute). Null = signed out.
    public async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        if (Session is null || transitioning) return null;
        if (access is not null && accessExpires > DateTime.UtcNow.AddMinutes(1)) return access;
        await refreshLock.WaitAsync(ct);
        try
        {
            if (access is not null && accessExpires > DateTime.UtcNow.AddMinutes(1)) return access;
            if (Session is null) return null;
            HttpResponseMessage res;
            try { res = await http.PostAsJsonAsync(new Uri(Server, "api/auth/refresh"), new { refreshToken = Session.RefreshToken }, ct); }
            catch (HttpRequestException) { return access; } // offline: keep the old token, retry later
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                await SignOutCoreAsync();
                return null;
            }
            if (!res.IsSuccessStatusCode) return access;
            var dto = (await res.Content.ReadFromJsonAsync<LoginDto>(Json, ct))!;
            access = dto.AccessToken; accessExpires = dto.AccessTokenExpiresAt.ToUniversalTime();
            Session = Session with { RefreshToken = dto.RefreshToken };
            store.Save(Session);
            return access;
        }
        finally { refreshLock.Release(); }
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var generation = Generation;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessTokenAsync(ct) ?? throw new InvalidOperationException("Signed out.");
            if (generation != Generation) throw new InvalidOperationException("Account changed.");
            using var req = new HttpRequestMessage(method, new Uri(Server, path.TrimStart('/')));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = JsonContent.Create(body, options: Json);
            var res = await http.SendAsync(req, ct);
            if (generation != Generation) { res.Dispose(); throw new InvalidOperationException("Account changed."); }
            if (res.StatusCode != HttpStatusCode.Unauthorized || attempt == 1) return res;
            access = null; // force a renewal and try once more
            res.Dispose();
        }
        throw new InvalidOperationException("unreachable");
    }

    /// Like <see cref="SendAsync"/> for a prepared body (multipart upload). Retries once after a renewal.
    public async Task<HttpResponseMessage> SendContentAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct = default)
    {
        var generation = Generation;
        var buffered = await content.ReadAsByteArrayAsync(ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessTokenAsync(ct) ?? throw new InvalidOperationException("Signed out.");
            if (generation != Generation) throw new InvalidOperationException("Account changed.");
            using var req = new HttpRequestMessage(method, new Uri(Server, path.TrimStart('/')));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new ByteArrayContent(buffered);
            foreach (var h in content.Headers) body.Headers.TryAddWithoutValidation(h.Key, h.Value);
            req.Content = body;
            var res = await http.SendAsync(req, ct);
            if (generation != Generation) { res.Dispose(); throw new InvalidOperationException("Account changed."); }
            if (res.StatusCode != HttpStatusCode.Unauthorized || attempt == 1) return res;
            access = null;
            res.Dispose();
        }
        throw new InvalidOperationException("unreachable");
    }

    public async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(HttpMethod.Get, path, null, ct);
            if (!res.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(HttpMethod.Get, path, null, ct);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch { return null; }
    }

    private static async Task<T?> SafeRead<T>(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadFromJsonAsync<T>(Json, ct); } catch { return default; }
    }
}
