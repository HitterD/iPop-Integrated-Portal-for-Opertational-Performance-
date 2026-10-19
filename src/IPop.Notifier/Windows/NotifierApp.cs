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
    private IPop.Notifier.Remote.RemoteHost? remoteHost;
    private readonly Dictionary<Guid, IPop.Notifier.Remote.ViewerWindow> viewers = [];
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
        device.Beat += () => Post(() => { api.WebBaseOverride = device.WebBase; devicesItem!.Visible = device.IctTools; UpdateTray(); });
        settings.Changed += _ => Post(UpdateTray);

        Toasts.Init();
        Toasts.Open += path => Post(() => OpenIPop(path));
        Toasts.OpenIdesk += id => _ = OpenMyTicketInIdeskAsync(id);
        Toasts.ChatReply += (room, text) => _ = SendChatAsync(room, text);
        Toasts.TicketReply += (ticket, text) => _ = SendTicketReplyAsync(ticket, text);

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

        tick.Tick += (_, _) => { popups.Release(); PinTray(); };
        tick.Start();

        if (api.IsSignedIn) _ = StartSessionAsync();
        else ShowSignIn();

        foreach (var a in args.Where(a => a.StartsWith(AppInfo.UrlScheme + ":", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(AppInfo.FilesScheme + ":", StringComparison.OrdinalIgnoreCase)))
            HandleCommand(a);
        // Every start re-registers the link schemes (an update may move the exe).
        Integration.RegisterUrlScheme();
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
        feed.TicketChatRead += refs => Post(() => { foreach (var r in refs) Toasts.RemoveTicket(r); _ = RefreshUnreadAsync(); });
        feed.ChatToast += t => Post(() => OnChatToast(t));
        feed.Remote += (ev, n) => Post(() => OnRemote(ev, n));
        feed.ConnectionChanged += c => Post(() => { connected = c; UpdateTray(); });
        try { await feed.StartAsync(ct); } catch (OperationCanceledException) { }
        await RefreshUnreadAsync();
    }

    private void OnRemote(string ev, System.Text.Json.Nodes.JsonNode n)
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

    private IPop.Notifier.Remote.RemoteHost NewRemoteHost()
    {
        var h = new IPop.Notifier.Remote.RemoteHost(api, (t, b, i) => tray.ShowBalloonTip(5000, t, b, i));
        // While ICT is connected the tray icon wears the green ring (mockup remote: always-on cue).
        h.ActiveChanged += on => Post(() => { remoteActive = on; UpdateTray(); });
        return h;
    }

    private bool remoteActive;

    /// Devices → Remote help / View only: ask the PC, then open the viewer.
    private async Task StartRemoteAsync(Guid deviceId, string mode)
    {
        try
        {
            using var res = await api.SendAsync(HttpMethod.Post, "api/notifier/remote/sessions", new { deviceId, mode });
            var body = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync());
            if (!res.IsSuccessStatusCode) { Post(() => tray.ShowBalloonTip(5000, "Remote help", body?["message"]?.GetValue<string>() ?? "Couldn't start remote help.", ToolTipIcon.Warning)); return; }
            var id = Guid.Parse(body!["sessionId"]!.GetValue<string>());
            Post(() =>
            {
                var v = new IPop.Notifier.Remote.ViewerWindow(api, id, new { type = "start", sessionId = id, status = body["status"]?.GetValue<string>(),
                    machine = body["machine"]?.GetValue<string>(), user = body["user"]?.GetValue<string>() });
                viewers[id] = v;
                v.FormClosed += (_, _) => viewers.Remove(id);
                v.Show();
            });
        }
        catch { Post(() => tray.ShowBalloonTip(5000, "Remote help", "Couldn't reach iPop.", ToolTipIcon.Warning)); }
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

    /// Reply typed in an iDesk toast: posted to the ticket chat in iDesk as me (requester or assigned agent).
    private async Task SendTicketReplyAsync(string ticket, string text)
    {
        try
        {
            using var res = await api.SendAsync(HttpMethod.Post, $"api/tickets/{ticket}/reply", new { content = text });
            if (res.IsSuccessStatusCode) { Post(() => Toasts.RemoveTicket(ticket)); return; }
            Post(() => tray.ShowBalloonTip(4000, "iDesk", "Your reply wasn't sent. Open the ticket in iDesk to try again.", ToolTipIcon.Warning));
        }
        catch { Post(() => tray.ShowBalloonTip(4000, "iDesk", "Your reply wasn't sent (no connection).", ToolTipIcon.Warning)); }
    }

    private async Task RefreshUnreadAsync()
    {
        var n = await api.GetJsonAsync("api/notifications/v1/unread-count");
        if (n?["unread"] is { } u) { unread = u.GetValue<int>(); Post(UpdateTray); }
    }

    private int pinTries;
    /// Windows records the tray icon a moment after it first appears; keep trying for about a minute.
    private void PinTray()
    {
        if (pinTries < 0 || pinTries++ > 12) return;
        if (TrayPin.PromoteOnce(SessionStore.Folder)) { pinTries = -1; return; }
        if (!TrayPin.IsWindows11 && pinTries == 2)
        {
            tray.ShowBalloonTip(6000, "iPop Notifier is running", "It lives here in the tray. Click ˄ and drag the iPop icon onto the taskbar to keep it visible.", ToolTipIcon.Info);
            pinTries = -1;
        }
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
        var status = remoteActive ? "ICT is connected" : !api.IsSignedIn ? "signed out" : !connected ? "connecting…" : s.Popups != "on" ? "popups paused" : count > 0 ? $"{count} unread" : "up to date";
        var text = "iPop Notifier" + (AppInfo.Flavour.Length > 0 ? " (" + AppInfo.Flavour + ")" : "") + " — " + status;
        tray.Text = text.Length > 63 ? text[..63] : text;
        var old = tray.Icon;
        tray.Icon = remoteActive ? AppIcon.Remote : AppIcon.Tray(count > 0);
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
        devices.RemoteRequested += (id, mode) => _ = StartRemoteAsync(id, mode);
        devices.Show();
    }

    private void OpenIPop(string path) => _ = Integration.OpenTargetAsync(api, path);

    /// My own ticket on the iDesk site: iPop knows its iDesk link (signed in through "Sign in with iPop").
    private async Task OpenMyTicketInIdeskAsync(string id)
    {
        var url = (await api.GetJsonAsync($"api/tickets/{id}"))?["url"]?.GetValue<string>();
        Post(() => { if (url is { Length: > 0 }) Integration.OpenUrl(url); else OpenIPop($"/tickets/{id}"); });
    }

    /// From a second start or an ipop-notifier:// link:
    ///   ipop-notifier://explorer?nas=spj&amp;path=/sja/SJA SPJ/ICT/a.xlsx → File Explorer (company NAS only)
    ///   ipop-notifier://inbox | show
    public void HandleCommand(string line)
    {
        line = line.Trim().Trim('"');
        if (line is "show" or "--background" || line.Length == 0) { if (line == "show") ShowInbox(); return; }
        if (!Uri.TryCreate(line, UriKind.Absolute, out var u)) return;
        var files = u.Scheme.Equals(AppInfo.FilesScheme, StringComparison.OrdinalIgnoreCase);
        if (!files && !u.Scheme.Equals(AppInfo.UrlScheme, StringComparison.OrdinalIgnoreCase)) return;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        // ipop://open?server=spj&path=… (iPop Files) is the same request as ipop-notifier://explorer?nas=spj&path=…
        var host = files && u.Host.Equals("open", StringComparison.OrdinalIgnoreCase) ? "explorer" : files ? "" : u.Host.ToLowerInvariant();
        switch (host)
        {
            case "explorer":
                if (files) q["nas"] = q["server"];
                var unc = Integration.UncPath(q["nas"] ?? "", q["path"] ?? "");
                if (unc is null || !Integration.OpenInExplorer(unc))
                    tray.ShowBalloonTip(4000, "iPop Notifier", "This link isn't on a company NAS, so it can't open in File Explorer.", ToolTipIcon.Warning);
                break;
            case "inbox": ShowInbox(); break;
            case "settings": ShowSettings(); break;
            case "devices": ShowDevices(); break;
            // ipop-notifier://remote?device=<id>&mode=view — same as Devices → Remote help (the server still checks
            // the ICT role, and the user still has to allow it).
            case "remote" when device.IctTools && Guid.TryParse(q["device"], out var dev):
                _ = StartRemoteAsync(dev, q["mode"] == "view" ? "view" : "control");
                break;
        }
    }

    // ── updates ──────────────────────────────────────────────────────────────

    /// Checks the update feed shortly after start and every 10 minutes. A new version is downloaded in the background
    /// and installed straight away (the app restarts by itself, signed in) unless remote help is running or the
    /// person is typing a reply in a popup — then it waits for the next check.
    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(45));
        while (true)
        {
            await UpdateAsync(manual: false);
            await Task.Delay(TimeSpan.FromMinutes(10));
        }
    }

    private bool Busy() => remoteActive || viewers.Count > 0;

    /// Velopack feed on the iPop server (/download/notifier/). Downloads quietly and applies on the next start,
    /// or right away when the user asked.
    private async Task UpdateAsync(bool manual)
    {
        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(AppInfo.UpdateFeed ?? new Uri(api.Server, "download/notifier/").ToString()));
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
            Log.Info("update", $"found {next.TargetFullRelease.Version}, downloading");
            await mgr.DownloadUpdatesAsync(next);
            if (!manual && Busy()) { Log.Info("update", "downloaded; waiting until remote help is over"); return; }
            Log.Info("update", "installing and restarting");
            Post(() => tray.ShowBalloonTip(3000, "iPop Notifier", $"Updating to {next.TargetFullRelease.Version}… it restarts by itself.", ToolTipIcon.Info));
            await Task.Delay(1500);
            mgr.ApplyUpdatesAndRestart(next);
        }
        catch (Exception ex)
        {
            Log.Warn("update", "check failed: " + ex.GetType().Name + " " + ex.Message);
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
