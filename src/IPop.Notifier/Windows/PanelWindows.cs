using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Tray Inbox flyout (mockup v1 §6): sits above the tray, hides when it loses focus.
public sealed class InboxWindow : BridgedWindow
{
    private readonly Func<object> context;
    public event Action<string, JsonNode?>? Command;

    public InboxWindow(IPopClient api, Func<object> context) : base("inbox.html", new Size(400, 560), api, showInTaskbar: false)
    {
        this.context = context;
        TopMost = true;
    }

    public void PlaceAtTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var gap = (int)(12 * DeviceDpi / 96.0);
        Location = new Point(area.Right - Width - gap, area.Bottom - Height - gap);
    }

    public new void Refresh() => Send(new { type = "context", ctx = context() });

    protected override void OnReady() => Refresh();

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

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
    private readonly SettingsService settings;
    private readonly Func<object> account;
    public event Action? SignOut;
    public event Action? PopupStyleChanged;

    public SettingsWindow(IPopClient api, SettingsService settings, Func<object> account) : base("settings.html", new Size(760, 640), api)
    {
        this.settings = settings;
        this.account = account;
        Text = "iPop Notifier — Settings";
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnReady() => Send(new { type = "settings", settings = settings.Current, account = account() });

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
            default: base.HandleMessage(type, msg); break;
        }
    }
}

/// First start (mockup v1 §8 left): NIK + iPop password, once.
public sealed class SignInWindow : HtmlWindow
{
    private readonly IPopClient api;
    public event Action? SignedIn;

    public SignInWindow(IPopClient api) : base("signin.html", new Size(440, 660))
    {
        this.api = api;
        Text = "iPop Notifier — Sign in";
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnReady() => Send(new { type = "init", server = api.Session?.Server ?? AppInfo.DefaultServer, version = AppInfo.Version });

    protected override async void HandleMessage(string type, JsonNode? msg)
    {
        if (type != "signIn") { base.HandleMessage(type, msg); return; }
        var nik = msg?["nik"]?.GetValue<string>()?.Trim() ?? "";
        var password = msg?["password"]?.GetValue<string>() ?? "";
        var server = msg?["server"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(server) || !Uri.TryCreate(server, UriKind.Absolute, out var su) || su.Scheme is not ("https" or "http"))
            server = AppInfo.DefaultServer;
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

/// ICT / Admin only (mockup v2 §3): signed-in PCs with computer name, IP and Windows user. Remote help is phase 3.
public sealed class DevicesWindow(IPopClient api) : BridgedWindow("devices.html", new Size(900, 600), api)
{
    protected override void HandleMessage(string type, JsonNode? msg)
    {
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

    protected override void OnHandleCreated(EventArgs e)
    {
        Text = "iPop Notifier — Devices";
        StartPosition = FormStartPosition.CenterScreen;
        base.OnHandleCreated(e);
    }
}
