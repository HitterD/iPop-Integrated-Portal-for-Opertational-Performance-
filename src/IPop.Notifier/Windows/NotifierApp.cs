using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using Velopack;
using Velopack.Sources;

namespace IPop.Notifier.Windows;

/// The tray app: icon + menu, sign-in, live feed, popups, Inbox, Settings, ICT Devices, links and self-update.
public sealed class NotifierApp : ApplicationContext
{
    private readonly SessionStore store = new();
    private readonly LocalState state = LocalState.Load();
    private readonly IPopClient api;
    private readonly SettingsService settings;
    private readonly DeviceReporter device;
    private readonly PopupManager popups;
    private readonly NotifyIcon tray;
    private readonly Control ui = new();
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 5000 };
    private CancellationTokenSource session = new();
    private Feed? feed;
    private InboxWindow? inbox;
    private SettingsWindow? settingsWindow;
    private DevicesWindow? devices;
    private SignInWindow? signIn;
    private int unread;
    private bool connected;
    private readonly ToolStripMenuItem devicesItem;
    private readonly ToolStripMenuItem popupsItem;

    public NotifierApp(string[] args)
    {
        ui.CreateControl();
        api = new IPopClient(store);
        settings = new SettingsService(api, state);
        device = new DeviceReporter(api);
        popups = new PopupManager(api, settings, state, device);
        api.SignedOut += () => Post(OnSignedOut);
        popups.CountChanged += UpdateTray;
        device.Beat += () => Post(() => { devicesItem!.Visible = device.IctTools; UpdateTray(); });
        settings.Changed += _ => Post(UpdateTray);

        Toasts.Init();
        Toasts.Open += path => Post(() => OpenIPop(path));
        Toasts.ChatReply += (room, text) => _ = SendChatAsync(room, text);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        menu.Items.Add("Open Inbox", null, (_, _) => ShowInbox());
        menu.Items.Add("Open iPop", null, (_, _) => OpenIPop("/dashboard"));
        menu.Items.Add("Show all windows", null, (_, _) => popups.ShowAll());
        popupsItem = new ToolStripMenuItem("Pause popups for 1 hour", null, (_, _) => TogglePause());
        menu.Items.Add(popupsItem);
        menu.Items.Add(new ToolStripSeparator());
        devicesItem = new ToolStripMenuItem("Devices (ICT)", null, (_, _) => ShowDevices()) { Visible = false };
        menu.Items.Add(devicesItem);
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Check for updates", null, (_, _) => _ = UpdateAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit iPop Notifier", null, (_, _) => Quit());
        menu.Opening += (_, _) => popupsItem.Text = settings.Current.Popups == "on" ? "Pause popups for 1 hour" : "Turn popups back on";

        tray = new NotifyIcon { Icon = AppIcon.Icon, Text = "iPop Notifier", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowInbox(); };

        tick.Tick += (_, _) => popups.Release();
        tick.Start();

        if (api.IsSignedIn) _ = StartSessionAsync();
        else ShowSignIn();

        foreach (var a in args.Where(a => a.StartsWith(AppInfo.UrlScheme + ":", StringComparison.OrdinalIgnoreCase)))
            HandleCommand(a);
        _ = UpdateLoopAsync();
    }

    public void Post(Action a)
    {
        if (ui.IsDisposed) return;
        if (ui.InvokeRequired) ui.BeginInvoke(a); else a();
    }

    // ── session ──────────────────────────────────────────────────────────────

    private async Task StartSessionAsync()
    {
        session.Cancel();
        session = new CancellationTokenSource();
        var ct = session.Token;
        await settings.LoadAsync(ct);
        _ = device.RunAsync(ct);
        feed = new Feed(api, state);
        feed.Received += n => Post(() => { popups.Show(n); _ = RefreshUnreadAsync(); });
        feed.ChatToast += t => Post(() => OnChatToast(t));
        feed.ConnectionChanged += c => Post(() => { connected = c; UpdateTray(); });
        try { await feed.StartAsync(ct); } catch (OperationCanceledException) { }
        await RefreshUnreadAsync();
    }

    private void OnChatToast(ToastPayload t)
    {
        var s = settings.Effective(DateTime.UtcNow);
        if (s.DeliveryFor("chat") == "off") return;
        var urgent = t.RequireInteraction;
        if (!urgent && (s.Popups != "on" || popups.Held(new Notice(Guid.Empty, "chat", "chat.message", t.Title, t.Body, "normal", t.Url, DateTime.UtcNow), s))) return;
        Toasts.ShowChat(t);
        Sounds.Play(t.Title.Contains(" · ") ? "group" : "dm", s);
    }

    private async Task SendChatAsync(string room, string text)
    {
        try
        {
            using var res = await api.SendAsync(HttpMethod.Post, $"api/chat/rooms/{room}/messages",
                new { content = text, idempotencyKey = Guid.NewGuid() });
            if (!res.IsSuccessStatusCode) Post(() => tray.ShowBalloonTip(4000, "iPop", "Your reply wasn't sent. Open the chat to try again.", ToolTipIcon.Warning));
        }
        catch { Post(() => tray.ShowBalloonTip(4000, "iPop", "Your reply wasn't sent (no connection).", ToolTipIcon.Warning)); }
    }

    private async Task RefreshUnreadAsync()
    {
        var n = await api.GetJsonAsync("api/notifications/v1/unread-count");
        if (n?["unread"] is { } u) { unread = u.GetValue<int>(); Post(UpdateTray); }
    }

    private void ShowSignIn()
    {
        if (signIn is { IsDisposed: false }) { signIn.Activate(); return; }
        signIn = new SignInWindow(api);
        signIn.SignedIn += () => Post(() => { _ = StartSessionAsync(); tray.ShowBalloonTip(3000, "iPop Notifier", "You're signed in. Notifications will appear here.", ToolTipIcon.Info); });
        signIn.Show();
        signIn.Activate();
    }

    private void OnSignedOut()
    {
        session.Cancel();
        _ = feed?.StopAsync();
        popups.CloseAll();
        inbox?.Hide();
        unread = 0;
        UpdateTray();
        ShowSignIn();
    }

    // ── tray, windows ────────────────────────────────────────────────────────

    private void UpdateTray()
    {
        var s = settings.Current;
        var count = unread;
        connected = feed?.Connected ?? connected;
        var status = !api.IsSignedIn ? "signed out" : !connected ? "connecting…" : s.Popups != "on" ? "popups paused" : count > 0 ? $"{count} unread" : "up to date";
        var text = "iPop Notifier — " + status;
        tray.Text = text.Length > 63 ? text[..63] : text;
        var old = tray.Icon;
        tray.Icon = AppIcon.Tray(count > 0);
        if (old is not null && old != AppIcon.Icon && old != tray.Icon) old.Dispose();
        if (inbox is { IsDisposed: false, Visible: true }) inbox.Refresh();
    }

    private object InboxContext() => new
    {
        name = device.DisplayName ?? api.Session?.Name ?? "",
        unread,
        popups = settings.Current.Popups == "on",
        held = state.Held,
        ict = device.IctTools,
        connected = feed?.Connected ?? connected,
    };

    private void ShowInbox()
    {
        if (!api.IsSignedIn) { ShowSignIn(); return; }
        if (inbox is null || inbox.IsDisposed)
        {
            inbox = new InboxWindow(api, InboxContext);
            inbox.Command += (type, msg) => Post(() => OnInboxCommand(type, msg));
        }
        inbox.PlaceAtTray();
        inbox.Show();
        inbox.Activate();
        inbox.Refresh();
    }

    private void OnInboxCommand(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "settings": ShowSettings(); break;
            case "devices": ShowDevices(); break;
            case "popups": TogglePause(); break;
            case "openNotice":
                // Re-open a notification from the Inbox as its own window (works for held/missed ones too).
                var n = System.Text.Json.JsonSerializer.Deserialize<InboxItem>(msg?["notice"], IPopClient.Json);
                if (n is null) return;
                state.Shown.Remove(n.Id);
                state.Held.Remove(n.Id);
                var notice = Notice.From(n);
                popups.Show(notice with { Priority = "high" });
                break;
        }
    }

    private void TogglePause()
    {
        var s = settings.Current;
        var next = s.Popups == "on" ? s with { Popups = "until", OffUntil = DateTime.UtcNow.AddHours(1) } : s with { Popups = "on", OffUntil = null };
        _ = settings.SaveAsync(next);
    }

    private void ShowSettings()
    {
        if (!api.IsSignedIn) { ShowSignIn(); return; }
        if (settingsWindow is { IsDisposed: false }) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(api, settings, () => new
        {
            name = device.DisplayName ?? api.Session?.Name ?? "",
            server = api.Server.ToString(),
            version = AppInfo.Version,
            computer = Environment.MachineName,
        });
        settingsWindow.SignOut += () => _ = api.SignOutAsync();
        settingsWindow.PopupStyleChanged += () => Post(popups.Restyle);
        settingsWindow.Show();
    }

    private void ShowDevices()
    {
        if (!device.IctTools) return;
        if (devices is { IsDisposed: false }) { devices.Activate(); return; }
        devices = new DevicesWindow(api);
        devices.Show();
    }

    private void OpenIPop(string path) => Integration.OpenUrl(new Uri(api.Server, path.TrimStart('/')).ToString());

    /// From a second start or an ipop-notifier:// link:
    ///   ipop-notifier://explorer?nas=spj&amp;path=/sja/SJA SPJ/ICT/a.xlsx → File Explorer (company NAS only)
    ///   ipop-notifier://inbox | show
    public void HandleCommand(string line)
    {
        line = line.Trim().Trim('"');
        if (line is "show" or "--background" || line.Length == 0) { if (line == "show") ShowInbox(); return; }
        if (!Uri.TryCreate(line, UriKind.Absolute, out var u) || !u.Scheme.Equals(AppInfo.UrlScheme, StringComparison.OrdinalIgnoreCase)) return;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        switch (u.Host.ToLowerInvariant())
        {
            case "explorer":
                var unc = Integration.UncPath(q["nas"] ?? "", q["path"] ?? "");
                if (unc is null || !Integration.OpenInExplorer(unc))
                    tray.ShowBalloonTip(4000, "iPop Notifier", "This link isn't on a company NAS, so it can't open in File Explorer.", ToolTipIcon.Warning);
                break;
            case "inbox": ShowInbox(); break;
            case "settings": ShowSettings(); break;
        }
    }

    // ── updates ──────────────────────────────────────────────────────────────

    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        while (true)
        {
            await UpdateAsync(manual: false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    /// Velopack feed on the iPop server (/download/notifier/). Downloads quietly and applies on the next start,
    /// or right away when the user asked.
    private async Task UpdateAsync(bool manual)
    {
        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(new Uri(api.Server, "download/notifier/").ToString()));
            if (!mgr.IsInstalled)
            {
                if (manual) Post(() => tray.ShowBalloonTip(3000, "iPop Notifier", "Updates work in the installed app only.", ToolTipIcon.Info));
                return;
            }
            var next = await mgr.CheckForUpdatesAsync();
            if (next is null)
            {
                if (manual) Post(() => tray.ShowBalloonTip(3000, "iPop Notifier", $"You have the latest version ({AppInfo.Version}).", ToolTipIcon.Info));
                return;
            }
            await mgr.DownloadUpdatesAsync(next);
            if (manual) mgr.ApplyUpdatesAndRestart(next);
            else mgr.WaitExitThenApplyUpdates(next, silent: true, restart: false);
        }
        catch
        {
            if (manual) Post(() => tray.ShowBalloonTip(3000, "iPop Notifier", "Couldn't check for updates right now.", ToolTipIcon.Warning));
        }
    }

    private void Quit()
    {
        tray.Visible = false;
        session.Cancel();
        popups.CloseAll();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            tick.Dispose();
            ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
