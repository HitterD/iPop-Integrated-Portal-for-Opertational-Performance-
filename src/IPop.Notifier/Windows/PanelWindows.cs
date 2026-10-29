using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Tray Inbox flyout (mockup v1 §6): sits above the tray, hides when it loses focus.
public sealed class InboxWindow : BridgedWindow
{
    public static readonly Size InboxSize = new(480, 720);
    private readonly Func<object> context;
    public event Action<string, JsonNode?>? Command;

    public InboxWindow(IPopClient api, Func<object> context) : base("inbox.html", InboxSize, api, showInTaskbar: false)
    {
        this.context = context;
        // Fixed size (logical px, scaled to this screen like the popups); only Maximize changes it.
        FitStandardSize(InboxSize);
        Maximizable = true;
        TopMost = true;
    }

    public void PlaceAtTray()
    {
        if (WindowState == FormWindowState.Maximized) return; // maximised: stays maximised until restored
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var gap = (int)(12 * DeviceDpi / 96.0);
        Location = new Point(area.Right - Width - gap, area.Bottom - Height - gap);
    }

    public new void Refresh() => Send(new { type = "context", ctx = context() });

    /// Connection changes reach an open Inbox straight away (it used to keep "Reconnecting…" until reopened).
    public void Connection(bool up) => Send(new { type = "connection", connected = up });

    protected override void OnReady() => Refresh();

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // The flyout hides when you click elsewhere; maximised it behaves as a normal window.
        if (WindowState != FormWindowState.Maximized) Hide();
    }

    protected override void OnWindowStateChanged() => TopMost = !Maximized;

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "close": Hide(); break;
            case "settings": case "popups": case "openNotice": case "devices":
                Command?.Invoke(type, msg);
                if (type is "settings" or "devices") Hide();
                break;
            default: base.HandleMessage(type, msg); break;
        }
    }
}

/// Settings (mockup v1 §7) — Popups (incl. the v1/v2 style choice), Sounds, Modules, General, Account.
public sealed class SettingsWindow : BridgedWindow
{
    public static readonly Size SettingsSize = new(960, 720);
    private readonly SettingsService settings;
    private readonly Func<object> account;
    public event Action? SignOut;
    public event Action? PopupStyleChanged;
    /// General › About: "Check for updates" and, when a version is ready, "Update now".
    public event Action? CheckUpdates;
    public event Action? UpdateNow;

    public SettingsWindow(IPopClient api, SettingsService settings, Func<object> account) : base("settings.html", SettingsSize, api)
    {
        this.settings = settings;
        this.account = account;
        Text = "iPop Notifier — Settings";
        FitStandardSize(SettingsSize);
        Maximizable = true;
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnReady() => Send(new { type = "settings", settings = settings.Current, account = account() });

    /// The answer of a "Check for updates" click: notInstalled | upToDate | ready (+ version) | failed.
    public void UpdateResult(object result) { if (!IsDisposed) Send(new { type = "updateResult", result }); }

    protected override async void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "save":
                var json = msg?["settings"]?.ToJsonString();
                var next = NotifierSettings.Parse(json);
                var styleChanged = next.Style != settings.Current.Style;
                var ok = await settings.SaveAsync(next);
                Send(new { type = "saved", ok, settings = settings.Current });
                if (styleChanged) PopupStyleChanged?.Invoke();
                break;
            case "preview":
                Sounds.Play(msg?["sound"]?.GetValue<string>() ?? "system", settings.Current with { Sound = true, Volume = msg?["volume"]?.GetValue<int>() ?? settings.Current.Volume });
                break;
            case "signOut":
                SignOut?.Invoke();
                Close();
                break;
            case "checkUpdates": CheckUpdates?.Invoke(); break;
            case "updateNow": UpdateNow?.Invoke(); break;
            default: base.HandleMessage(type, msg); break;
        }
    }
}

/// First start (mockup v1 §8 left): NIK + iPop password, once.
public sealed class SignInWindow : HtmlWindow
{
    private readonly IPopClient api;
    public event Action? SignedIn;
    /// "Sign in with browser": the default browser opens iPop's "iPop Notifier wants to connect" page (one tab).
    /// true = "Open browser again" (after 20 s of waiting): a fresh attempt, one new tab.
    public event Action<bool, string?>? WithBrowser;
    /// "Paste code from browser": the short code that page shows when the browser couldn't reach the app.
    public event Action<string?>? PastedCode;

    public SignInWindow(IPopClient api) : base("signin.html", new Size(480, 760))
    {
        this.api = api;
        Text = "iPop Notifier — Sign in";
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnReady() => Send(new { type = "init", server = api.Session?.Server ?? AppInfo.DefaultServer, version = AppInfo.Version });

    /// The "Server…" field: an absolute http(s) address, else null (the default server).
    private static string? ServerOf(JsonNode? msg)
    {
        var server = msg?["server"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(server) || !Uri.TryCreate(server, UriKind.Absolute, out var su) || su.Scheme is not ("https" or "http")) return null;
        return su.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    protected override async void HandleMessage(string type, JsonNode? msg)
    {
        if (type == "withBrowser") { WithBrowser?.Invoke(msg?["again"]?.GetValue<bool>() == true, ServerOf(msg)); return; }
        if (type == "pasteCode") { PastedCode?.Invoke(msg?["code"]?.GetValue<string>()); return; }
        if (type != "signIn") { base.HandleMessage(type, msg); return; }
        var nik = msg?["nik"]?.GetValue<string>()?.Trim() ?? "";
        var password = msg?["password"]?.GetValue<string>() ?? "";
        var server = ServerOf(msg) ?? AppInfo.DefaultServer;
        Integration.SetStartWithWindows(msg?["autostart"]?.GetValue<bool>() != false);
        try
        {
            var r = await api.SignInAsync(server, nik, password, CancellationToken.None);
            if (r.Ok) { SignedIn?.Invoke(); Close(); }
            else Send(new { type = "error", message = r.Error });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Send(new { type = "error", message = "Can't reach iPop. Check the network and try again." });
        }
    }
}

/// "iPop Notifier X is ready": Update now / Later (1.3.0). Closing = Later.
public sealed class UpdateWindow : HtmlWindow
{
    private readonly object info;
    public event Action? Now;
    public event Action? Later;
    private bool answered;

    public UpdateWindow(object info) : base("update.html", new Size(520, 440))
    {
        this.info = info;
        Text = "iPop Notifier — Update";
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    protected override void OnReady() => Send(new { type = "update", info });

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            // A blocked/failed attempt leaves the window open; closing it afterwards is still Later.
            case "now": Now?.Invoke(); break;
            case "later": case "close": answered = true; Later?.Invoke(); Close(); break;
            default: base.HandleMessage(type, msg); break;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!answered) Later?.Invoke();
        base.OnFormClosed(e);
    }
}

/// ICT / Admin only (mockup v2 §3): signed-in PCs with computer name, IP and Windows user. Remote help is phase 3.
public sealed class DevicesWindow : BridgedWindow
{
    public static readonly Size DevicesSize = new(1100, 720);
    public DevicesWindow(IPopClient api) : base("devices.html", DevicesSize, api)
    {
        Text = "iPop Notifier — Devices";
        FitStandardSize(DevicesSize);
        Maximizable = true; // read by CreateParams when the handle is created, so it must be set here
        StartPosition = FormStartPosition.CenterScreen;
    }

    public event Action<Guid, string>? RemoteRequested;

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        if (type == "remote" && Guid.TryParse(msg?["deviceId"]?.GetValue<string>(), out var dev)) { RemoteRequested?.Invoke(dev, msg?["mode"]?.GetValue<string>() == "view" ? "view" : "control"); return; }
        if (type != "rdp") { base.HandleMessage(type, msg); return; }
        // Windows Remote Desktop to a plain IPv4 address only (the value comes from the device list).
        var host = msg?["host"]?.GetValue<string>() ?? "";
        if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("mstsc.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/v:" + ip);
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}
