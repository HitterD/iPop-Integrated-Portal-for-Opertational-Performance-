using System.Text.Json.Nodes;
using IPop.Notifier.Core;

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
    private readonly ToolStripMenuItem pendingItem;

    public NotifierApp(string[] args)
    {
        ui.CreateControl();
        api = new IPopClient(store);
        settings = new SettingsService(api, state);
        Theme.LargeText = settings.Current.TextSize == "large";
        Theme.Mode = settings.Current.Theme;
        Theme.FollowWindows();
        device = new DeviceReporter(api);
        popups = new PopupManager(api, settings, state, device);
        api.SignedOut += () => Post(OnSignedOut);
        api.BeforeSessionChanged = TeardownSessionAsync;
        api.ConfirmAccountSwitch = (nik, name) =>
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => result.TrySetResult(MessageBox.Show($"Switch iPop Notifier to {name ?? nik}?\nNotifications and remote sessions for the current account will close.", "Switch account", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
            return result.Task;
        };
        popups.CountChanged += UpdateTray;
        device.Beat += () => Post(() => { api.WebBaseOverride = device.WebBase; devicesItem!.Visible = device.IctTools; UpdateTray(); });
        settings.Changed += s => Post(() => { Theme.LargeText = s.TextSize == "large"; Theme.Mode = s.Theme; UpdateTray(); });

        Toasts.Init();
        Toasts.Open += path => Post(() => OpenIPop(path));
        Toasts.OpenIdesk += id => _ = OpenMyTicketInIdeskAsync(id);
        Toasts.ChatReply += (room, text) => _ = SendChatAsync(room, text);
        Toasts.TicketReply += (ticket, text) => _ = SendTicketReplyAsync(ticket, text);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        menu.Items.Add(new ToolStripMenuItem($"iPop Notifier · v{AppInfo.Version}") { Enabled = false });
        menu.Items.Add("Open Inbox", null, (_, _) => ShowInbox());
        menu.Items.Add("Open iPop", null, (_, _) => OpenIPop("/dashboard"));
        menu.Items.Add("Show all windows", null, (_, _) => popups.ShowAll());
        // Notifications from while the PC was off, shown one by one: count + "skip" (they stay unread in the Inbox).
        pendingItem = new ToolStripMenuItem("Skip pending notifications (keep unread)", null, (_, _) => popups.SkipBacklog()) { Visible = false };
        menu.Items.Add(pendingItem);
        popupsItem = new ToolStripMenuItem("Pause popups for 1 hour", null, (_, _) => TogglePause());
        menu.Items.Add(popupsItem);
        menu.Items.Add(new ToolStripSeparator());
        devicesItem = new ToolStripMenuItem("Devices (ICT)", null, (_, _) => ShowDevices()) { Visible = false };
        menu.Items.Add(devicesItem);
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckNowAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit iPop Notifier", null, (_, _) => Quit());
        menu.Opening += (_, _) => popupsItem.Text = settings.Current.Popups == "on" ? "Pause popups for 1 hour" : "Turn popups back on";

        tray = new NotifyIcon { Icon = AppIcon.Icon, Text = "iPop Notifier", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowInbox(); };

        tick.Tick += (_, _) => { popups.Release(); PinTray(); };
        tick.Start();

        if (api.IsSignedIn) _ = StartSessionAsync();
        else { ShowSignIn(); if (args.Contains("--first-run")) OpenBrowserSignIn(); }

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
        var generation = api.Generation;
        Toasts.Account = api.Session?.Server + "|" + api.Session?.Nik + "|" + generation;
        await settings.LoadAsync(ct);
        if (ct.IsCancellationRequested || generation != api.Generation || !api.IsSignedIn) return;
        _ = device.RunAsync(ct);
        feed = new Feed(api, state);
        feed.Received += n => Post(() => { if (generation != api.Generation || ct.IsCancellationRequested) return; popups.Show(n); _ = RefreshUnreadAsync(); });
        feed.Backlog += items => Post(() => { if (generation != api.Generation || ct.IsCancellationRequested) return; popups.ShowBacklog(items); _ = RefreshUnreadAsync(); });
        feed.TicketChatRead += refs => Post(() => { foreach (var r in refs) Toasts.RemoveTicket(r); _ = RefreshUnreadAsync(); });
        feed.ReadElsewhere += () => _ = RefreshUnreadAsync();
        feed.ChatToast += t => Post(() => { if (generation == api.Generation && !ct.IsCancellationRequested) OnChatToast(t); });
        feed.Remote += (ev, n) => Post(() => { if (generation == api.Generation && !ct.IsCancellationRequested) OnRemote(ev, n); });
        feed.ReleasePublished += OnReleasePublished;
        feed.ConnectionChanged += c => Post(() => { connected = c; UpdateTray(); if (inbox is { IsDisposed: false }) inbox.Connection(c); });
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
        signIn.WithBrowser += (again, server) => Post(() => { if (!api.IsSignedIn) api.SignInServer = server; OpenBrowserSignIn(again); });
        signIn.Show();
        signIn.Activate();
    }

    // ── "Sign in with browser": a connect card in the browser (1.4.2) ───────────────────────────────────
    //  click → new request id + PKCE verifier (kept here) → POST link/intent {deviceKey, challenge, requestId, pcName}
    //  → an open iPop tab on this PC hears it (AppHub), takes it and shows the card itself; nobody within ~5 s: the
    //  app reserves it and opens exactly ONE tab on /notifier/connect?request=…&opened=1
    //  → the person clicks Connect → link/poll (every second) returns the code once → redeem with the verifier
    //  → link/connected, so the card says "Connected". Cancel on the card → poll says "denied".
    //  No ipop-notifier:// launch from the web at all: browsers silently block one a script starts in a background tab.
    //  Clicks while an attempt runs do nothing; after 20 s "Open browser again" starts a fresh request + one tab.

    /// Tabs on this PC have this long to take the click before the app opens one itself.
    private static readonly TimeSpan TabGrace = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? browserAttempt;
    private DateTime browserAttemptAt;

    private void OpenBrowserSignIn() => OpenBrowserSignIn(again: false);

    private void OpenBrowserSignIn(bool again)
    {
        if (api.IsSignedIn) return;
        if (browserAttempt is { IsCancellationRequested: false } && (!again || DateTime.UtcNow - browserAttemptAt < TimeSpan.FromSeconds(20)))
        {
            signIn?.Activate();
            return;
        }
        browserAttempt?.Cancel();
        var cts = browserAttempt = new CancellationTokenSource(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10));
        browserAttemptAt = DateTime.UtcNow;
        _ = BrowserAttemptAsync(again, cts);
    }

    private void BrowserState(string state, string? message = null) => Post(() => signIn?.Send(new { type = "browserState", state, message }));

    private async Task BrowserAttemptAsync(bool again, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        BrowserState("asking");
        var link = api.BeginBrowserLink();
        if (link is null || !await api.RegisterBrowserLinkAsync(link, ct))
        {
            if (ct.IsCancellationRequested) return;
            Log.Warn("signin", "browser: could not register the connect request");
            BrowserState("failed", "Can't reach iPop. Check the network and try again.");
            cts.Cancel();
            return;
        }
        var opened = false;
        var start = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // "Open browser again" opens its tab at once; a first click gives open tabs ~5 s to take it.
                var reserve = !opened && (again || DateTime.UtcNow - start >= TabGrace);
                var poll = await api.PollBrowserLinkAsync(link.RequestId, reserve, ct);
                if (ct.IsCancellationRequested) return;
                switch (poll?.State)
                {
                    case "approved" when poll.Code is { Length: 43 } code:
                        Log.Info("signin", "browser: Connect was clicked");
                        BrowserState("approving");
                        await SignInWithCodeAsync(code, link.RequestId);
                        return;
                    case "denied":
                        Log.Info("signin", "browser: cancelled in the browser");
                        api.ForgetBrowserLink(link.RequestId);
                        BrowserState("denied", "Cancelled in the browser. You can try again or sign in here.");
                        cts.Cancel();
                        return;
                    case "expired" or "collected":
                        api.ForgetBrowserLink(link.RequestId);
                        BrowserState("expired", "The request expired. Choose Sign in with browser to try again.");
                        cts.Cancel();
                        return;
                    case "claimed" when !opened:
                        if (!tabShown) { tabShown = true; Log.Info("signin", "browser: an open iPop tab shows the card"); BrowserState("tab"); }
                        break;
                    case "pending" when reserve:
                        opened = true;
                        Log.Info("signin", "browser: no iPop tab answered, opening one");
                        Post(() => OpenIPop("/notifier/connect?request=" + link.RequestId + "&opened=1"));
                        BrowserState("opened");
                        break;
                }
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally { tabShown = false; }
    }

    private bool tabShown;

    private async Task SignInWithCodeAsync(string code, string requestId)
    {
        var previous = api.Session?.Nik;
        IPopClient.LinkResult r;
        try { r = await api.SignInWithCodeAsync(api.Server.ToString(), code, requestId, CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { r = new(false, "Can't reach iPop. Check the network and try again.", null, false); }
        Post(() =>
        {
            browserAttempt?.Cancel();
            if (!r.Ok)
            {
                Log.Warn("signin", "browser link refused: " + r.Error);
                if (signIn is { IsDisposed: false }) BrowserState("failed", r.Error ?? "Couldn't sign in from the browser.");
                else tray.ShowBalloonTip(5000, "iPop Notifier", r.Error ?? "Couldn't sign in from the browser.", ToolTipIcon.Warning);
                return;
            }
            // The card that approved it learns the app is connected (only that account can read it).
            _ = api.ConfirmBrowserLinkAsync(requestId);
            if (signIn is { IsDisposed: false }) signIn.Close();
            _ = StartSessionAsync();
            Integration.SetStartWithWindows(true);
            Toasts.ShowSignedIn(r.Name ?? api.Session?.Name ?? "", switched: previous is not null && r.Switched);
        });
    }

    /// Complete teardown before credentials change, even on same-account relinking.
    private async Task TeardownSessionAsync()
    {
        session.Cancel();
        var oldFeed = feed; feed = null;
        if (oldFeed is not null) await oldFeed.StopAsync();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                // End using the old captured token; the normal API refresh gate is already held.
                if (remoteHost is not null)
                {
                    var remoteId = remoteHost.SessionId;
                    await remoteHost.EndAsync("account", notifyServer: false);
                    if (remoteId is { } id) await api.EndRemoteForTransitionAsync(id);
                }
                remoteHost = null;
                foreach (var pair in viewers.ToList())
                {
                    await api.EndRemoteForTransitionAsync(pair.Key);
                    pair.Value.Close();
                }
                viewers.Clear(); remoteActive = false;
                Toasts.Account = null;
                Toasts.ClearAll();
                popups.CloseAll();
                inbox?.Close(); inbox = null;
                settingsWindow?.Close(); settingsWindow = null;
                devices?.Close(); devices = null;
                devicesItem.Visible = false;
                device.Reset();
                unread = 0; connected = false;
                state.Shown.Clear(); state.Held.Clear(); state.LastShownAt = null; state.SettingsJson = null;
                state.Save();
                UpdateTray();
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        await done.Task;
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
        var pending = popups.BacklogCount;
        pendingItem.Visible = pending > 0;
        pendingItem.Text = $"Skip {pending} pending notification{(pending == 1 ? "" : "s")} (keep unread)";
        var status = remoteActive ? "ICT is connected" : !api.IsSignedIn ? "signed out" : !connected ? "connecting…" : s.Popups != "on" ? "popups paused"
            : pending > 0 ? $"{pending} pending · {count} unread" : count > 0 ? $"{count} unread" : "up to date";
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
        settingsWindow.CheckUpdates += () => _ = CheckFromSettingsAsync();
        settingsWindow.UpdateNow += () => Post(() => { if (updater?.Pending is { } p) ShowUpdate(p); });
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
            // 1.4.2: sign-in no longer comes in through a link (no prepare/signin cases); see BrowserAttemptAsync.
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

    private Updater? updater;
    private UpdateWindow? updateWindow;
    private readonly SemaphoreSlim updateKick = new(0, 1);

    /// Checks shortly after start, every 6 hours, and as soon as iPop says a release was published
    /// ("notifierUpdate" on the hub). A new version downloads in the background; then the person is asked
    /// (Update now / Later). <see cref="IPop.Application.Notifier.NotifierUpdatePolicy"/> decides when to ask again,
    /// when to install anyway (after a day, PC idle) and that a mandatory release installs at once.
    private async Task UpdateLoopAsync()
    {
        updater ??= new Updater(api);
        await Task.Delay(TimeSpan.FromSeconds(45));
        var lastCheck = DateTime.MinValue;
        while (true)
        {
            if (DateTime.UtcNow - lastCheck >= TimeSpan.FromHours(6) || kicked || updater.ContextChanged)
            {
                kicked = false;
                lastCheck = DateTime.UtcNow;
                await updater.CheckAsync();
            }
            // A call in iPop (browser or phone) counts as busy too: no restart in the middle of it.
            if (updater.Pending is not null) inCall = await InCallAsync();
            Post(StepUpdate);
            // Re-evaluate every minute (Later timer, idle install); a publish signal wakes it at once.
            await updateKick.WaitAsync(TimeSpan.FromMinutes(1));
        }
    }

    private volatile bool kicked;

    /// iPop published a release: check now (spread over a minute so every PC doesn't download at the same second).
    private void OnReleasePublished()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 60)));
            kicked = true;
            if (updateKick.CurrentCount == 0) try { updateKick.Release(); } catch (SemaphoreFullException) { }
        });
    }

    private bool RemoteBusy() => remoteActive || viewers.Count > 0;
    private bool Busy() => RemoteBusy() || inCall != false;
    private bool? inCall;

    private async Task<bool?> InCallAsync()
    {
        if (!api.IsSignedIn) return false;
        // GetJsonAsync conflates an idle JSON null with HTTP/network errors. Only a successful
        // idle response is safe; unknown status must never restart a call on another device.
        try
        {
            using var res = await api.SendAsync(HttpMethod.Get, "api/calls/active");
            var body = await res.Content.ReadAsStringAsync();
            return IPop.Application.Notifier.NotifierUpdatePolicy.ReadCallStatus((int)res.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Log.Warn("update", "call status unavailable: " + ex.Message);
            return null;
        }
    }

    private void StepUpdate()
    {
        if (updater?.Pending is not { } ready) return;
        switch (updater.NextStep(Busy()))
        {
            case IPop.Application.Notifier.NotifierUpdatePolicy.Step.Ask:
                ShowUpdate(ready);
                break;
            case IPop.Application.Notifier.NotifierUpdatePolicy.Step.InstallNow:
                tray.ShowBalloonTip(4000, "iPop Notifier", $"Updating to {ready.Version}… it restarts by itself.", ToolTipIcon.Info);
                _ = InstallSoonAsync();
                break;
            case IPop.Application.Notifier.NotifierUpdatePolicy.Step.InstallQuietly:
                Log.Info("update", "installing while the PC is idle");
                _ = InstallSoonAsync();
                break;
        }
    }

    private void ShowUpdate(Updater.Ready ready)
    {
        if (updateWindow is { IsDisposed: false }) { updateWindow.Activate(); return; }
        try { updater!.MarkAsked(); }
        catch (Exception ex)
        {
            Log.Warn("update", "could not save prompt timing: " + ex.Message);
            tray.ShowBalloonTip(4000, "iPop Notifier", "Couldn't save the update reminder. Please try again later.", ToolTipIcon.Warning);
            return;
        }
        updater.Asking = true;
        updateWindow = new UpdateWindow(new { version = ready.Version, current = AppInfo.Version, notes = ready.Notes });
        updateWindow.Now += () => _ = InstallSoonAsync(manual: true);
        updateWindow.Later += () =>
        {
            updater.Asking = false;
            try { updater.MarkAsked(); }
            catch (Exception ex) { Log.Warn("update", "could not save Later timing: " + ex.Message); }
        };
        updateWindow.FormClosed += (_, _) => updater.Asking = false;
        updateWindow.Show();
    }

    private bool installing;
    private async Task InstallSoonAsync(bool manual = false)
    {
        if (installing || updater is null) return;
        installing = true;
        try
        {
            await Task.Delay(1500);
            var result = await updater.InstallAndRestartAsync(InCallAsync, RemoteBusy);
            // A manual click can arrive while an automatic install attempt is already checking.
            // Recover any open prompt on a blocked attempt, regardless of who started that attempt.
            if (result != IPop.Application.Notifier.NotifierUpdatePolicy.InstallResult.Installed
                && (manual || updateWindow is { IsDisposed: false }))
                UpdateInstallFeedback(result == IPop.Application.Notifier.NotifierUpdatePolicy.InstallResult.Busy
                    ? "Finish your call or remote help session, then choose Update now again."
                    : "Couldn't confirm whether a call is active. Check your connection, then try again.");
        }
        catch (Exception ex)
        {
            Log.Warn("update", "install failed: " + ex.Message);
            UpdateInstallFeedback("The update couldn't be installed. Please try again later.");
        }
        finally { installing = false; }
    }

    private void UpdateInstallFeedback(string message) => Post(() =>
    {
        updateWindow?.Send(new { type = "installBlocked", message });
        tray.ShowBalloonTip(4000, "iPop Notifier", message, ToolTipIcon.Warning);
    });

    /// Settings › General › About → Check for updates: the answer shows in the row itself (no balloon).
    private async Task CheckFromSettingsAsync()
    {
        updater ??= new Updater(api);
        Updater.Result r;
        try { r = await updater.CheckAsync(); }
        catch (Exception ex) { Log.Warn("update", "check failed: " + ex.Message); r = Updater.Result.Failed; }
        Post(() => settingsWindow?.UpdateResult(r switch
        {
            Updater.Result.NotInstalled => new { state = "notInstalled", version = (string?)null },
            Updater.Result.UpToDate => new { state = "upToDate", version = (string?)AppInfo.Version },
            Updater.Result.Ready when updater.Pending is { } p => new { state = "ready", version = (string?)p.Version },
            _ => new { state = "failed", version = (string?)null },
        }));
    }

    /// Tray → Check for updates.
    private async Task CheckNowAsync()
    {
        updater ??= new Updater(api);
        var r = await updater.CheckAsync();
        Post(() =>
        {
            switch (r)
            {
                case Updater.Result.NotInstalled: tray.ShowBalloonTip(3000, "iPop Notifier", "Updates work in the installed app only.", ToolTipIcon.Info); break;
                case Updater.Result.UpToDate: tray.ShowBalloonTip(3000, "iPop Notifier", $"You have the latest version ({AppInfo.Version}).", ToolTipIcon.Info); break;
                case Updater.Result.Failed: tray.ShowBalloonTip(3000, "iPop Notifier", "Couldn't check for updates right now.", ToolTipIcon.Warning); break;
                case Updater.Result.Ready when updater.Pending is { } p: ShowUpdate(p); break;
            }
        });
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
