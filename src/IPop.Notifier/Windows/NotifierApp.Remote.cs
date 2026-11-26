using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Remote;

namespace IPop.Notifier.Windows;

/// Remote help wiring (moved out of NotifierApp.cs): the hub events, the user's side (RemoteHost), and the helper's
/// side — Devices / Remote window → Connect → (password for an unattended PC) → viewer. BIGIS #8 v3: a connect the
/// company policy stops (Remote off, blocked, excluded, too many) shows a banner in the Remote window, a PC someone
/// else is helping asks "Join view-only"; nothing goes to a tray balloon any more. Errors after that (offline,
/// declined, locked…) open the viewer on the matching screen.
public sealed partial class NotifierApp
{
    private RemoteHost? remoteHost;
    private readonly Dictionary<Guid, IPop.Notifier.Remote.ViewerWindow> viewers = [];
    private RemoteBookWindow? remoteBook;
    private RemotePasswordWindow? passwordWindow;
    /// Passwords "remembered for this session" (in memory only, per PC). Kept only while that PC's viewer is open:
    /// dropped when its session ends or its window closes, and on sign-out.
    private readonly Dictionary<string, string> rememberedPasswords = [];
    private bool remoteActive;
    private JsonNode? remotePrefs;

    private void OnRemote(string ev, JsonNode n)
    {
        remoteHost ??= NewRemoteHost();
        Guid.TryParse(n["sessionId"]?.GetValue<string>(), out var id);
        switch (ev)
        {
            case "remoteRequest": _ = HostRequestAsync(n); break;
            case "remoteAnswer": if (viewers.TryGetValue(id, out var v)) v.Answer(n["status"]?.GetValue<string>() ?? ""); break;
            case "remoteEnded":
                if (viewers.TryGetValue(id, out var w)) { w.Ended(n["endedBy"]?.GetValue<string>()); rememberedPasswords.Clear(); }
                remoteHost.OnEnded(n);
                break;
        }
    }

    /// The user's own default ticks (Settings › Remote) are read fresh for each prompt.
    private async Task HostRequestAsync(JsonNode n)
    {
        var prefs = await LoadRemotePrefsAsync();
        var d = prefs?["userDefaults"];
        Post(() =>
        {
            if (remoteHost is null) return;
            remoteHost.UserDefaults = d is JsonObject o ? string.Join(',', new[] { "control", "clipboard", "files" }.Where(k => o[k]?.GetValue<bool>() == true)) : null;
            remoteHost.OnRequest(n, api.Session?.DeviceKey ?? "");
        });
    }

    private async Task<JsonNode?> LoadRemotePrefsAsync()
    {
        try { remotePrefs = await api.GetJsonAsync("api/me/ui-prefs/remote") ?? remotePrefs; } catch { }
        return remotePrefs;
    }

    private RemoteHost NewRemoteHost()
    {
        var h = new RemoteHost(api, (t, b, i) => tray.ShowBalloonTip(5000, t, b, i));
        // While ICT is connected the tray icon wears the green ring (mockup remote: always-on cue).
        h.ActiveChanged += on => Post(() => { remoteActive = on; UpdateTray(); });
        return h;
    }

    private void ShowRemoteBook()
    {
        if (!device.IctTools) return;
        if (remoteBook is { IsDisposed: false }) { remoteBook.Activate(); return; }
        remoteBook = new RemoteBookWindow(api, device.DisplayName);
        remoteBook.ConnectRequested += (id, target, join) => _ = ConnectAsync(id, target, "control", null, askUser: false, join);
        remoteBook.FormClosed += (_, _) => remoteBook = null;
        remoteBook.Show();
    }

    private void CloseRemoteWindows()
    {
        remoteBook?.Close(); remoteBook = null;
        passwordWindow?.Close(); passwordWindow = null;
        rememberedPasswords.Clear();
    }

    /// Devices → Remote help / View only.
    private Task StartRemoteAsync(Guid deviceId, string mode) => ConnectAsync(deviceId, null, mode, null, askUser: false);

    /// One connect attempt. 200 → viewer; 428 → ask for the password; 403 wrong / 423 locked → the dialog or the
    /// viewer's locked screen; 409 busy → "Join view-only"; policy stops → banner; offline etc. → the viewer's screen.
    private async Task ConnectAsync(Guid? deviceId, string? target, string mode, string? password, bool askUser, bool join = false)
    {
        try
        {
            var key = deviceId?.ToString() ?? target ?? "";
            password ??= rememberedPasswords.GetValueOrDefault(key);
            using var res = await api.SendAsync(HttpMethod.Post, "api/notifier/remote/sessions",
                new { deviceId, target, mode, password, askUser, fromMachine = Environment.MachineName, joinViewOnly = join });
            JsonNode? body = null;
            try { body = JsonNode.Parse(await res.Content.ReadAsStringAsync()); } catch { }
            var status = (int)res.StatusCode;
            await LoadRemotePrefsAsync();
            Post(() => OnConnectResult(status, body, deviceId, target, mode, key, password));
        }
        catch { Post(() => Banner(new { code = "net", title = "Couldn't reach iPop", message = "Check the network and try again." })); }
    }

    /// Shows the problem in the Remote window (opened if needed) — never a tray balloon.
    private void Banner(object b)
    {
        ShowRemoteBook();
        remoteBook?.Banner(b);
    }

    private void OnConnectResult(int status, JsonNode? body, Guid? deviceId, string? target, string mode, string key, string? password)
    {
        var code = body?["code"]?.GetValue<string>();
        if (status == 200 && Guid.TryParse(body?["sessionId"]?.GetValue<string>(), out var id))
        {
            passwordWindow?.Close(); passwordWindow = null;
            if (password is not null && rememberNext) rememberedPasswords[key] = password;
            OpenViewer(id, body!, null, deviceId, target, mode);
            return;
        }
        rememberedPasswords.Remove(key);
        if (status is 428 || (status == 403 && code == "wrong")) { AskPassword(body, wrong: status == 403, deviceId, target, mode, key); return; }
        passwordWindow?.Close(); passwordWindow = null;
        if (code == "busy") { ShowRemoteBook(); remoteBook?.Busy(new { busyBy = body?["busyBy"]?.GetValue<string>(), machine = body?["machine"]?.GetValue<string>(), deviceId, target }); return; }
        if (code is "off" or "blocked" or "excluded" or "rate" or "own" or "own-session" or "unknown" or "ambiguous" or "conflict" || status is 429 or 400)
        {
            Banner(new { code, title = body?["title"]?.GetValue<string>() ?? body?["message"]?.GetValue<string>() ?? "Couldn't connect", message = body?["title"] is null ? "" : body?["message"]?.GetValue<string>() });
            return;
        }
        if (status == 403 && code is null) { Banner(new { code = "blocked", title = "Remote help is switched off for you", message = "An Administrator blocked remote help for your account." }); return; }
        // Offline, locked and anything else: the viewer's own screen says what happened.
        OpenViewer(Guid.Empty, body ?? new JsonObject(), new { status, body }, deviceId, target, mode);
    }

    private bool rememberNext = true;

    private void AskPassword(JsonNode? body, bool wrong, Guid? deviceId, string? target, string mode, string key)
    {
        if (passwordWindow is not { IsDisposed: false })
        {
            passwordWindow = new RemotePasswordWindow();
            passwordWindow.FormClosed += (_, _) => passwordWindow = null;
            passwordWindow.Submitted += (pw, remember) => { rememberNext = remember; _ = ConnectAsync(deviceId, target, mode, pw, askUser: false); };
            passwordWindow.AskUser += () => { passwordWindow?.Close(); _ = ConnectAsync(deviceId, target, mode, null, askUser: true); };
            passwordWindow.Show();
        }
        passwordWindow.Present(body?["machine"]?.GetValue<string>(), body?["remoteId"]?.GetValue<string>(),
            device.DisplayName is { } n ? $"{n.Split(' ')[0]} (ICT)" : null, body?["triesLeft"]?.GetValue<int>(), wrong, body?["lockMinutes"]?.GetValue<int>() ?? 15);
        passwordWindow.Activate();
    }

    private void OpenViewer(Guid id, JsonNode body, object? problem, Guid? deviceId, string? target, string mode)
    {
        var v = new IPop.Notifier.Remote.ViewerWindow(api, id, new
        {
            type = "start", sessionId = id, status = body["status"]?.GetValue<string>(), machine = body["machine"]?.GetValue<string>() ?? target ?? "Remote help",
            user = body["user"]?.GetValue<string>() ?? "", remoteId = body["remoteId"]?.GetValue<string>(), problem,
            askSeconds = body["askSeconds"]?.GetValue<int>(), prefs = remotePrefs?.DeepClone(),
        });
        v.ConfigureFiles(remotePrefs?["saveDir"]?.GetValue<string>(), 0);
        var key = deviceId?.ToString() ?? target ?? "";
        if (id != Guid.Empty) { viewers[id] = v; v.FormClosed += (_, _) => viewers.Remove(id); }
        v.FormClosed += (_, _) => rememberedPasswords.Remove(key);
        v.RetryRequested += () => { v.Close(); _ = ConnectAsync(deviceId, target, mode, null, askUser: false); };
        v.RemotePref += (k, val) => _ = SaveRemotePrefAsync(k, val);
        v.Show();
    }

    /// The viewer's toolbar changed Fit / 1:1 / Fill: remembered in Settings › Remote.
    private async Task SaveRemotePrefAsync(string key, string value)
    {
        if (key != "fit" || value is not ("fit" or "actual" or "fill")) return;
        var p = (await LoadRemotePrefsAsync())?.DeepClone() as JsonObject ?? [];
        p["fit"] = value;
        try { using var _ = await api.SendAsync(HttpMethod.Put, "api/me/ui-prefs/remote", p); remotePrefs = p; } catch { }
    }
}
