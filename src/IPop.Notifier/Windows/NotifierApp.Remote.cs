using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Remote;

namespace IPop.Notifier.Windows;

/// Remote help wiring (moved out of NotifierApp.cs, 1.8.0): the hub events, the user's side (RemoteHost), and the
/// helper's side — Devices / Remote window → Connect → (password for an unattended PC) → viewer. A connect that can't
/// start (offline, wrong password, locked) opens the viewer on the matching screen instead of a balloon.
public sealed partial class NotifierApp
{
    private RemoteHost? remoteHost;
    private readonly Dictionary<Guid, IPop.Notifier.Remote.ViewerWindow> viewers = [];
    private RemoteBookWindow? remoteBook;
    private RemotePasswordWindow? passwordWindow;
    /// Passwords "remembered for this session" (in memory only, per remote ID; gone when the app quits or signs out).
    private readonly Dictionary<string, string> rememberedPasswords = [];
    private bool remoteActive;

    private void OnRemote(string ev, JsonNode n)
    {
        remoteHost ??= NewRemoteHost();
        Guid.TryParse(n["sessionId"]?.GetValue<string>(), out var id);
        switch (ev)
        {
            case "remoteRequest": remoteHost.OnRequest(n, api.Session?.DeviceKey ?? ""); break;
            case "remoteAnswer": if (viewers.TryGetValue(id, out var v)) v.Answer(n["status"]?.GetValue<string>() ?? ""); break;
            case "remoteEnded":
                if (viewers.TryGetValue(id, out var w)) w.Ended(n["endedBy"]?.GetValue<string>());
                remoteHost.OnEnded(n);
                break;
        }
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
        remoteBook.ConnectRequested += (id, target) => _ = ConnectAsync(id, target, "control", null, askUser: false);
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
    /// viewer's locked screen; 409 offline and the rest → the viewer's error screen.
    private async Task ConnectAsync(Guid? deviceId, string? target, string mode, string? password, bool askUser)
    {
        try
        {
            var key = deviceId?.ToString() ?? target ?? "";
            password ??= rememberedPasswords.GetValueOrDefault(key);
            using var res = await api.SendAsync(HttpMethod.Post, "api/notifier/remote/sessions",
                new { deviceId, target, mode, password, askUser, fromMachine = Environment.MachineName });
            JsonNode? body = null;
            try { body = JsonNode.Parse(await res.Content.ReadAsStringAsync()); } catch { }
            var status = (int)res.StatusCode;
            Post(() => OnConnectResult(status, body, deviceId, target, mode, key, password));
        }
        catch { Post(() => tray.ShowBalloonTip(5000, "Remote help", "Couldn't reach iPop.", ToolTipIcon.Warning)); }
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
        if (status is 428 || (status == 403 && code == "wrong"))
        {
            AskPassword(body, wrong: status == 403, deviceId, target, mode, key);
            return;
        }
        if (status == 403 && code is null) { tray.ShowBalloonTip(5000, "Remote help", "Remote help is switched off for your account.", ToolTipIcon.Warning); return; }
        if (status == 429) { tray.ShowBalloonTip(5000, "Remote help", "Too many connects in a minute. Wait a moment.", ToolTipIcon.Warning); return; }
        if (status is 400 or 404 && code is "unknown" or "own" or "ambiguous" && remoteBook is { IsDisposed: false })
        {
            remoteBook.Toast(body?["message"]?.GetValue<string>() ?? "That computer isn't known.");
            return;
        }
        passwordWindow?.Close(); passwordWindow = null;
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
            device.DisplayName is { } n ? $"{n.Split(' ')[0]} (ICT)" : null, body?["triesLeft"]?.GetValue<int>(), wrong);
        passwordWindow.Activate();
    }

    private void OpenViewer(Guid id, JsonNode body, object? problem, Guid? deviceId, string? target, string mode)
    {
        var v = new IPop.Notifier.Remote.ViewerWindow(api, id, new
        {
            type = "start", sessionId = id, status = body["status"]?.GetValue<string>(), machine = body["machine"]?.GetValue<string>() ?? target ?? "Remote help",
            user = body["user"]?.GetValue<string>() ?? "", remoteId = body["remoteId"]?.GetValue<string>(), problem,
        });
        if (id != Guid.Empty) { viewers[id] = v; v.FormClosed += (_, _) => viewers.Remove(id); }
        v.RetryRequested += () => { v.Close(); _ = ConnectAsync(deviceId, target, mode, null, askUser: false); };
        v.Show();
    }
}
