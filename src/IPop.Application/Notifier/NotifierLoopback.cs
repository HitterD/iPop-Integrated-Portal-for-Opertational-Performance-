using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IPop.Application.Notifier;

/// iPop Notifier "Sign in with browser" (1.4.3), OAuth loopback style (like Claude Code, gh, gcloud):
///
///   1. The app makes a PKCE verifier + challenge (S256) and a random state, listens on
///      http://127.0.0.1:{random port}/callback (this PC only) and opens the default browser at
///      /notifier/connect?state&amp;challenge&amp;redirect&amp;pc. Exactly one new tab per attempt.
///   2. The signed-in person sees "iPop Notifier wants to connect" and clicks Authorize: the server mints a
///      one-time code bound to the challenge and that account, and the tab navigates to redirect?code&amp;state.
///      Decline navigates to redirect?error=access_denied&amp;state.
///   3. The app checks the state, redeems the code with its verifier and answers the tab with a small page.
///      Fallback: the card shows a short code the person pastes into the app (redeemed with the same verifier).
/// Nothing is broadcast to other tabs and no network address is compared: only the app on this PC holds the
/// verifier and listens on its loopback port. Shared by the Notifier (linked source), the API and the web card.
public static class NotifierLoopback
{
    public const string CallbackPath = "/callback";
    /// The only redirect the web card and the server accept: plain http to 127.0.0.1, an explicit port, /callback.
    private static readonly Regex Redirect = new(@"^http://127\.0\.0\.1:([1-9][0-9]{3,4})/callback$", RegexOptions.CultureInvariant);

    public static bool IsValidRedirect(string? redirect)
    {
        if (redirect is null || redirect.Length > 40) return false;
        var m = Redirect.Match(redirect);
        return m.Success && int.TryParse(m.Groups[1].Value, out var port) && port is >= 1024 and <= 65535;
    }

    /// 32 random bytes, base64url without padding (43 characters).
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static bool IsSecret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static bool SameSecret(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// The PC name the card shows: plain text, no control characters, at most 64.
    public static string CleanPc(string? pc)
    {
        var clean = new string((pc ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return clean.Length > 0 ? clean : "this PC";
    }

    // ── short code for the paste fallback ────────────────────────────────────────────────────────────────

    /// No 0/O, 1/I/L, U: easy to read aloud and to type.
    public const string ShortAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    public static string NewShortCode()
    {
        Span<char> c = stackalloc char[8];
        for (var i = 0; i < c.Length; i++) c[i] = ShortAlphabet[RandomNumberGenerator.GetInt32(ShortAlphabet.Length)];
        return new string(c);
    }
    public static string FormatShortCode(string code) => code.Length == 8 ? code[..4] + "-" + code[4..] : code;
    /// What the person typed or pasted: case, spaces and dashes don't matter. Null = can't be a short code.
    public static string? NormalizeShortCode(string? typed)
    {
        if (typed is null) return null;
        var s = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
        return s.Length == 8 && s.All(ShortAlphabet.Contains) ? s : null;
    }

    public static string ConnectUrl(Uri web, string state, string challenge, string redirect, string pc) =>
        new Uri(web, "notifier/connect").ToString()
        + "?state=" + Uri.EscapeDataString(state)
        + "&challenge=" + Uri.EscapeDataString(challenge)
        + "&redirect=" + Uri.EscapeDataString(redirect)
        + "&pc=" + Uri.EscapeDataString(CleanPc(pc));

    public static string CallbackUrl(string redirect, string state, string? code = null, string? error = null) =>
        redirect + "?" + (code is not null ? "code=" + Uri.EscapeDataString(code) : "error=" + Uri.EscapeDataString(error ?? "access_denied"))
        + "&state=" + Uri.EscapeDataString(state);
}

/// What the browser brought back to the loopback callback, after the state matched.
public sealed record LoopbackCallback(string? Code, string? Error);
/// What the app answers the tab with.
public sealed record LoopbackAnswer(string Html, bool Done);

/// A tiny HTTP/1.1 server on 127.0.0.1 only (a raw TcpListener: no http.sys URL ACL, no admin), for one sign-in
/// attempt. GET /callback with the right state goes to <c>handle</c> exactly once; a wrong state gets an error page
/// and the listener keeps waiting; anything else is 404. It stops after the handled callback or its lifetime.
public sealed class LoopbackCallbackServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly string state;
    private readonly Func<LoopbackCallback, Task<LoopbackAnswer>> handle;
    private readonly CancellationTokenSource stop;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int handled; // 0 = waiting, 1 = a callback with the right state is being / was handled

    public int Port { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}{NotifierLoopback.CallbackPath}";
    /// Completes when the listener stops (handled, lifetime over, or disposed).
    public Task Finished => finished.Task;
    public bool Handled => Volatile.Read(ref handled) == 1;

    private LoopbackCallbackServer(TcpListener listener, string state, Func<LoopbackCallback, Task<LoopbackAnswer>> handle, TimeSpan lifetime)
    {
        this.listener = listener;
        this.state = state;
        this.handle = handle;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        stop = new CancellationTokenSource(lifetime);
        stop.Token.Register(() => { try { listener.Stop(); } catch { } finished.TrySetResult(); });
        _ = AcceptLoopAsync();
    }

    public static LoopbackCallbackServer Start(string state, Func<LoopbackCallback, Task<LoopbackAnswer>> handle, TimeSpan lifetime)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return new LoopbackCallbackServer(listener, state, handle, lifetime);
    }

    public void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { break; }
            _ = ServeAsync(client);
        }
        finished.TrySetResult();
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var net = client.GetStream();
            using var read = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            read.CancelAfter(TimeSpan.FromSeconds(10));
            var target = await ReadRequestTargetAsync(net, read.Token);
            if (target is null) { await WriteAsync(net, 400, LoopbackPages.Error("This request wasn't understood."), CancellationToken.None); return; }
            var (path, query) = Split(target);
            if (path != NotifierLoopback.CallbackPath) { await WriteAsync(net, 404, "", CancellationToken.None, text: true); return; }
            var q = ParseQuery(query);
            if (!NotifierLoopback.SameSecret(q.GetValueOrDefault("state"), state))
            {
                await WriteAsync(net, 400, LoopbackPages.Error("This sign-in didn't come from this iPop Notifier. Choose Sign in with browser in iPop Notifier and try again."), CancellationToken.None);
                return;
            }
            if (Interlocked.CompareExchange(ref handled, 1, 0) != 0)
            {
                await WriteAsync(net, 409, LoopbackPages.Handled(), CancellationToken.None);
                return;
            }
            LoopbackAnswer answer;
            try { answer = await handle(new LoopbackCallback(q.GetValueOrDefault("code"), q.GetValueOrDefault("error"))); }
            catch { answer = new(LoopbackPages.Error("iPop Notifier couldn't finish signing in. Try again from the app."), true); }
            if (!answer.Done) Volatile.Write(ref handled, 0);
            await WriteAsync(net, 200, answer.Html, CancellationToken.None);
            if (answer.Done) Stop();
        }
        catch { }
    }

    /// The request target of "GET /path?query HTTP/1.1"; null for anything else. Headers are read and ignored.
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream net, CancellationToken ct)
    {
        var buf = new byte[8192];
        var len = 0;
        while (len < buf.Length)
        {
            var n = await net.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) break;
            len += n;
            if (buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var head = Encoding.ASCII.GetString(buf, 0, len);
        var end = head.IndexOf("\r\n", StringComparison.Ordinal);
        if (end <= 0) return null;
        var parts = head[..end].Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static (string Path, string Query) Split(string target)
    {
        var i = target.IndexOf('?');
        return i < 0 ? (target, "") : (target[..i], target[(i + 1)..]);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            var v = eq < 0 ? "" : pair[(eq + 1)..];
            try { d.TryAdd(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(v.Replace('+', ' '))); } catch { }
        }
        return d;
    }

    private static async Task WriteAsync(NetworkStream net, int status, string body, CancellationToken ct, bool text = false)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Content-Type: {(text ? "text/plain" : "text/html")}; charset=utf-8\r\n" +
                   $"Content-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Referrer-Policy: no-referrer\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\r\n" +
                   "Connection: close\r\n\r\n";
        await net.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await net.WriteAsync(bytes, ct);
        await net.FlushAsync(ct);
    }
}

/// The small self-contained pages the app answers the browser tab with (inline CSS, light/dark, no external files).
public static class LoopbackPages
{
    public static string Success(string? name) => Page("ok", "iPop Notifier is connected",
        (name is { Length: > 0 } ? "Signed in as <b>" + WebUtility.HtmlEncode(name) + "</b>. " : "") + "You can close this tab.", close: true);
    public static string Cancelled() => Page("x", "Sign-in cancelled", "iPop Notifier was not connected. You can close this tab.", close: false);
    public static string Error(string message) => Page("warn", "iPop Notifier isn't connected", WebUtility.HtmlEncode(message), close: false);
    public static string Handled() => Page("info", "Already handled", "This sign-in was already handled. You can close this tab.", close: false);

    private static string Page(string kind, string title, string html, bool close)
    {
        var icon = kind switch
        {
            "ok" => "<path d=\"M5 12.5l4.5 4.5L19 7.5\"/>",
            "x" => "<path d=\"M7 7l10 10M17 7L7 17\"/>",
            "info" => "<path d=\"M12 11v6M12 7.5v.01\"/>",
            _ => "<path d=\"M12 8v5M12 16.5v.01\"/>",
        };
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<title>" + WebUtility.HtmlEncode(title) + " · iPop</title><style>"
            + ":root{color-scheme:light dark;--bg:#f5f5f4;--card:#fff;--ink:#1c1917;--ink3:#78716c;--line:#e7e5e4;--ok:#2e9d6b;--oks:#e4f4ea;--warn:#c0392b;--warns:#fdecea;--soft:#f0efed}"
            + "@media (prefers-color-scheme:dark){:root{--bg:#0f1412;--card:#1a201d;--ink:#f2f1ee;--ink3:#a8a29e;--line:#2b332f;--ok:#4cc38a;--oks:#173528;--warn:#f08a7e;--warns:#3a1d1a;--soft:#232a26}}"
            + "*{box-sizing:border-box}html,body{height:100%;margin:0}"
            + "body{display:grid;place-items:center;background:var(--bg);color:var(--ink);font:15px/1.5 \"Segoe UI\",system-ui,-apple-system,sans-serif;padding:24px}"
            + ".card{width:min(420px,100%);text-align:center;padding:34px 28px 28px;border-radius:22px;background:var(--card);border:1px solid var(--line);box-shadow:0 10px 36px -12px rgba(0,0,0,.18)}"
            + ".badge{width:60px;height:60px;margin:0 auto;border-radius:50%;display:grid;place-items:center;background:var(--soft);color:var(--ink3)}"
            + ".badge.ok{background:var(--oks);color:var(--ok)}.badge.warn{background:var(--warns);color:var(--warn)}"
            + ".badge svg{width:30px;height:30px;fill:none;stroke:currentColor;stroke-width:2.4;stroke-linecap:round;stroke-linejoin:round}"
            + "h1{font-size:21px;font-weight:680;margin:16px 0 6px;letter-spacing:-.01em}p{margin:0;color:var(--ink3)}p b{color:var(--ink);font-weight:650}"
            + ".brand{margin-top:22px;font-size:12.5px;color:var(--ink3)}"
            + "</style></head><body><main class=\"card\" role=\"status\">"
            + "<div class=\"badge " + kind + "\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\">" + icon + "</svg></div>"
            + "<h1>" + WebUtility.HtmlEncode(title) + "</h1><p>" + html + "</p>"
            + "<div class=\"brand\">iPop Notifier</div></main>"
            + (close ? "<script>setTimeout(function(){try{window.close()}catch(e){}},1500)</script>" : "")
            + "</body></html>";
    }
}
