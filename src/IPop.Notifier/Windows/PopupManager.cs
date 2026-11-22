using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Decides what happens to each notification and lays the windows out.
/// 1.5.0 priority tiers (docs/notifier/PRIORITY-150.md): Needs action → its own window; Messages → a toast;
/// For your info → nothing but the count. Window styles:
///  • "windows" (v2, default): one real window per notification, no limit (1.4.2: "20 popups = 20 windows"), never
///    minimised by the app. Cascaded 30 px from the screen corner, newest in front; when the next step would leave the
///    screen a new cascade column starts further left (then a new row), so every window stays visible and clickable.
///    All share the iPop taskbar button (same AppUserModelID) with a count badge. While held (meeting/full screen/
///    Focus/Busy) new windows open minimised and come out afterwards.
///  • "stack" (v1): glass cards stacked at the corner; as many as fit the screen height are shown, only the ones that
///    physically don't fit wait behind "+N more" and appear as cards close. While held they wait in the queue.
/// Nothing is ever dropped: every notification is also in the tray Inbox.
public sealed class PopupManager(IPopClient api, SettingsService settings, LocalState state, DeviceReporter device, Leaders leaders)
{
    private readonly List<NoticeWindow> open = [];
    private readonly Queue<Notice> waiting = new();
    /// Notifications that arrived while the PC was off: shown one at a time, oldest first ("1 of N pending").
    private readonly Queue<Notice> backlog = new();
    private NoticeWindow? backlogWindow;
    private int backlogTotal, backlogShown;
    public int BacklogCount => backlog.Count + (backlogWindow is null ? 0 : 1);
    private bool restyling;
    /// The screen the person is working on when the first popup of a group opens; every popup of that group stays on it
    /// (1.4.7 always used the primary monitor).
    private Rectangle? area;
    private Rectangle Area() => area ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
    public event Action? CountChanged;
    public int OpenCount => open.Count + waiting.Count;

    /// Unread "Needs action" in the Inbox (counted by the app): the number on the taskbar badge.
    public int ActCount
    {
        get => actCount;
        set { if (actCount == value) return; actCount = value; Badge(); }
    }
    private int actCount;

    /// What the popup pages get besides the notice (tiers, leaders, VIPs; set by the app).
    public Func<object>? PageContext { get; set; }

    /// Kind + tier with the settings and leaders of this moment.
    public Notice Classify(Notice n) => n.Classified(settings.Effective(DateTime.UtcNow), leaders);

    /// Busy (Windows Focus/full screen/meeting, or iPop status Busy) with "hold" chosen: popups and toasts wait.
    /// Needs action breaks through when "Let Needs action break through" is on.
    public bool Held(Notice n, NotifierSettings s)
    {
        if (n.NeedsAction && s.UrgentBreaksThrough) return false;
        return s.DuringMeetings == "hold" && (Native.UserIsBusy() || device.IPopBusy);
    }

    /// Everything still unread from while the PC was off. Only Needs action opens windows, one at a time (the next
    /// opens when that one is closed, read or answered). Messages collapse into toasts: one per conversation (chat,
    /// iDesk ticket chat) as before, one per module for the rest ("5 unread · newest title"). For your info stays in
    /// the Inbox.
    public void ShowBacklog(IReadOnlyList<Notice> items)
    {
        var s = settings.Effective(DateTime.UtcNow);
        var fresh = items.Where(n => !state.Shown.Contains(n.Id) && backlog.All(b => b.Id != n.Id)).Select(Classify).ToList();
        foreach (var n in fresh.Where(n => n.Tier == NotifierSettings.Info)) state.Shown.Add(n.Id);
        foreach (var group in fresh.Where(n => n.Tier == NotifierSettings.Msg).GroupBy(n => IsConversation(n) ? "c" + (n.TargetUrl ?? n.Id.ToString()) : "m" + n.Module))
        {
            var newest = group.MaxBy(n => n.At)!;
            var count = group.Count();
            foreach (var n in group) state.Shown.Add(n.Id);
            if (s.HideMessageToasts) continue;
            if (s.Popups != "on" || Held(newest, s)) { foreach (var n in group) Hold(n); continue; }
            var title = count > 1 ? $"{count} unread · " + newest.Title : newest.Title;
            Toasts.ShowNotice(newest with { Title = title }, api.Server);
        }
        foreach (var n in fresh.Where(n => n.NeedsAction)) backlog.Enqueue(n);
        backlogTotal = backlogShown + backlog.Count + (backlogWindow is null ? 0 : 1);
        state.Save();
        NextBacklog();
        CountChanged?.Invoke();
    }

    /// Chat and iDesk ticket chat: one toast per conversation even when many messages piled up.
    private static bool IsConversation(Notice n) => n.Module == "chat" || (n.Module == "tickets" && n.EventType == "ticket.message_created");

    /// Opens the next pending notification when none is on screen (and the person isn't in a meeting / paused).
    public void NextBacklog()
    {
        if (backlogWindow is { IsDisposed: false } || backlog.Count == 0) return;
        var s = settings.Effective(DateTime.UtcNow);
        var peek = Classify(backlog.Peek());
        if ((s.Popups != "on" && !(peek.NeedsAction && s.UrgentBreaksThrough)) || Held(peek, s)) return;
        while (backlog.Count > 0)
        {
            var n = Classify(backlog.Dequeue());
            if (state.Shown.Contains(n.Id)) continue;
            state.Shown.Add(n.Id);
            if (!n.NeedsAction) continue; // its category changed while it waited: it stays in the Inbox
            backlogShown++;
            var w = Open(n, s, minimized: false, pending: (backlogShown, Math.Max(backlogTotal, backlogShown)));
            backlogWindow = w;
            w.FormClosed += (_, _) => { if (backlogWindow == w) backlogWindow = null; NextBacklog(); CountChanged?.Invoke(); };
            Sounds.Play(Sounds.For(n), s);
            break;
        }
        if (backlog.Count == 0 && backlogWindow is null) { backlogTotal = 0; backlogShown = 0; }
        state.Save();
    }

    /// Tray "Skip pending": the queue is dropped from the screen; everything stays unread in the Inbox.
    public void SkipBacklog()
    {
        while (backlog.Count > 0) state.Shown.Add(backlog.Dequeue().Id);
        backlogTotal = 0; backlogShown = 0;
        state.Save();
        CountChanged?.Invoke();
    }

    /// Entry point for every notification (live or caught up). By tier: Needs action → its own window with sound
    /// (breaks through Busy/full screen/paused popups when "Let Needs action break through" is on); Messages → a toast
    /// with a soft sound (none at all with "Hide Messages toasts"; held while Busy or paused); For your info → nothing
    /// but the count.
    public void Show(Notice n)
    {
        if (state.Shown.Contains(n.Id)) return;
        state.Shown.Add(n.Id);
        if (state.LastShownAt is null || n.At > state.LastShownAt) state.LastShownAt = n.At;

        var s = settings.Effective(DateTime.UtcNow);
        n = n.Classified(s, leaders);
        var popupsOff = s.Popups != "on";
        if (n.Tier == NotifierSettings.Info) { state.Save(); CountChanged?.Invoke(); return; }
        var held = Held(n, s);

        if (n.Tier == NotifierSettings.Msg)
        {
            if (!s.HideMessageToasts)
            {
                if (popupsOff || held) Hold(n);
                else { Toasts.ShowNotice(n, api.Server); Sounds.Play(Sounds.For(n), s); }
            }
            state.Save();
            CountChanged?.Invoke();
            return;
        }
        if (popupsOff && !s.UrgentBreaksThrough) { Hold(n); state.Save(); return; }
        if (held) Hold(n);
        state.Save();
        ShowWindow(n, s, held);
    }

    /// A Needs action chat message that came as a "desktopToast" (a leader's / VIP's chat, an emergency): opens as a
    /// window. One window per conversation: the next message from the same chat refreshes the one already open.
    public void ShowChat(Notice n)
    {
        var s = settings.Effective(DateTime.UtcNow);
        var held = Held(n, s);
        if (s.Popups != "on" && !s.UrgentBreaksThrough) return; // the toast path holds it; the Inbox has it anyway
        ShowWindow(n, s, held);
    }

    private void ShowWindow(Notice n, NotifierSettings s, bool held)
    {
        if (n.Module == "chat" && n.TargetUrl is { } url
            && open.FirstOrDefault(w => !w.IsDisposed && w.Notice.Module == "chat" && w.Notice.TargetUrl == url) is { } same)
        {
            same.Update(n);
            if (!held) Sounds.Play(Sounds.For(n), s);
            return;
        }
        if (s.Style == "stack")
        {
            if (held || !StackHasRoom()) { waiting.Enqueue(n); UpdateMore(); CountChanged?.Invoke(); return; }
            Open(n, s, minimized: false);
        }
        else
        {
            Open(n, s, minimized: held);
        }
        if (!held) Sounds.Play(Sounds.For(n), s);
    }

    /// Snooze (5 min on a meeting reminder; 15 min / 1 h / tomorrow 08:00 from the Snooze menu): the server only hides
    /// it from the Inbox until then; nothing pushes it again, so the app brings the same notification back itself as
    /// Needs action. Kept in memory: after a restart it simply reappears in the Inbox.
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(5);
    private readonly HashSet<Guid> snoozed = [];
    private System.Windows.Forms.Timer? snoozeTimer;
    private readonly List<(Notice Notice, DateTime Due)> dueAgain = [];

    private void SnoozeAgain(Notice n, DateTime? until)
    {
        if (!snoozed.Add(n.Id)) return;
        var now = DateTime.UtcNow;
        var due = until is { } u && u > now && u < now.AddDays(8) ? u : now + SnoozeFor;
        dueAgain.Add((n, due));
        snoozeTimer ??= new System.Windows.Forms.Timer { Interval = 15_000 };
        snoozeTimer.Tick -= OnSnoozeTick;
        snoozeTimer.Tick += OnSnoozeTick;
        snoozeTimer.Start();
    }

    private void OnSnoozeTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var due in dueAgain.Where(d => d.Due <= now).ToList())
        {
            dueAgain.Remove(due);
            snoozed.Remove(due.Notice.Id);
            state.Shown.Remove(due.Notice.Id);
            state.Held.Remove(due.Notice.Id);
            Show(due.Notice with { Priority = "high" });
        }
        if (dueAgain.Count == 0) snoozeTimer?.Stop();
    }

    /// Inbox > click a notification: opens as its own window, always (also for held ones and Messages / For your info),
    /// without a sound and without changing what counts as shown or missed.
    public void OpenFromInbox(Notice n)
    {
        var s = settings.Effective(DateTime.UtcNow);
        state.Held.Remove(n.Id);
        Open(Classify(n), s with { Style = "windows" }, minimized: false);
        state.Save();
    }

    private void Hold(Notice n)
    {
        if (!state.Held.Contains(n.Id)) state.Held.Add(n.Id);
    }

    private NoticeWindow Open(Notice n, NotifierSettings s, bool minimized, (int Index, int Total)? pending = null)
    {
        if (open.Count == 0)
        {
            try { area = Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch { area = null; }
        }
        var w = new NoticeWindow(n, s.Style, api, pending, PageContext);
        w.FormClosed += (_, _) => Closed(w);
        w.Done += (_, action, until) => { if (action == "snooze") SnoozeAgain(w.Notice, until); };
        w.HeightChanged += _ => Layout();
        w.Activated += (_, _) => Front(w);
        open.Add(w);
        Place(w, s);
        if (minimized) w.WindowState = FormWindowState.Minimized;
        w.Show();
        Badge();
        UpdateMore();
        CountChanged?.Invoke();
        return w;
    }

    private void Front(NoticeWindow w)
    {
        if (open.Remove(w)) open.Add(w);
    }

    private void Closed(NoticeWindow w)
    {
        open.Remove(w);
        if (restyling) return;
        var s = settings.Effective(DateTime.UtcNow);
        // A card closed: the next waiting one takes its place (v1). Held ones wait for the user to be back.
        while (waiting.Count > 0 && StackHasRoom() && !Held(waiting.Peek(), s))
            Open(waiting.Dequeue(), s with { Style = "stack" }, minimized: false);
        Layout();
        Badge();
        UpdateMore();
        CountChanged?.Invoke();
    }

    /// Called every few seconds: when the user is back from a meeting, held windows/cards come out quietly.
    public void Release()
    {
        var s = settings.Effective(DateTime.UtcNow);
        if (Native.UserIsBusy() || device.IPopBusy) return;
        foreach (var w in open.Where(w => w.Style != "stack" && w.WindowState == FormWindowState.Minimized && state.Held.Contains(w.Notice.Id)).ToList())
        {
            state.Held.Remove(w.Notice.Id);
            w.WindowState = FormWindowState.Normal;
        }
        while (waiting.Count > 0 && StackHasRoom())
            Open(waiting.Dequeue(), s with { Style = "stack" }, minimized: false);
        NextBacklog();
    }

    /// Taskbar/tray "show all": restore every window, newest in front.
    public void ShowAll()
    {
        foreach (var w in open) { if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal; }
        open.LastOrDefault()?.Activate();
    }

    public void CloseAll()
    {
        dueAgain.Clear(); snoozed.Clear(); snoozeTimer?.Stop();
        waiting.Clear();
        backlog.Clear();
        actCount = 0;
        foreach (var w in open.ToList()) w.Close();
    }

    /// Leaders or settings changed: open windows get the new context (chips like "Your leader").
    public void RefreshContext()
    {
        foreach (var w in open.ToList()) if (!w.IsDisposed) w.SendContext();
    }

    /// The style changed in Settings: open windows/cards are rebuilt in the new style.
    public void Restyle()
    {
        var notices = open.Select(w => w.Notice).Concat(waiting).ToList();
        waiting.Clear();
        restyling = true;
        foreach (var w in open.ToList()) w.Close();
        restyling = false;
        open.Clear();
        var s = settings.Effective(DateTime.UtcNow);
        foreach (var n in notices)
        {
            if (s.Style == "stack" && !StackHasRoom()) waiting.Enqueue(n);
            else Open(n, s, minimized: false);
        }
        Layout();
    }

    private void Place(NoticeWindow w, NotifierSettings s)
    {
        var area = Area();
        var gap = Scale(w, 14);
        if (w.Style == "stack") { Layout(); return; }
        if (w.Height > area.Height - Scale(w, 28)) w.Height = area.Height - Scale(w, 28);
        if (w.Width > area.Width - Scale(w, 28)) w.Width = area.Width - Scale(w, 28);
        var i = open.Count(x => x.Style != "stack") - 1;
        var (px, py) = IPop.Application.Notifier.PopupLayout.CascadeSlot(i, w.Width, w.Height, area.Left, area.Top, area.Width, area.Height, gap, Scale(w, 30), Scale(w, 160), s.Position == "top-right");
        w.Location = new Point(px, py);
    }

    /// v1 stack: another card fits when every open card can keep its minimum height on the screen.
    private bool StackHasRoom()
    {
        var cards = open.Where(w => w.Style == "stack").ToList();
        if (cards.Count == 0) return true;
        var area = Area();
        var c = cards[0];
        return IPop.Application.Notifier.PopupLayout.StackFits(cards.Count + 1, area.Height, Scale(c, 14), Scale(c, 10), Scale(c, 150));
    }
    /// v1 stack: cards from the corner upwards (or downwards at the top), newest nearest the corner.
    /// v2 windows: one fixed size, capped to the screen.
    private void Layout()
    {
        var s = settings.Effective(DateTime.UtcNow);
        var area = Area();
        var cards = open.Where(w => w.Style == "stack").ToList();
        if (cards.Count > 0)
        {
            var gap = Scale(cards[0], 10);
            var edge = Scale(cards[0], 14);
            var y = s.Position == "top-right" ? area.Top + edge : area.Bottom - edge;
            // Cards share the column: each gets at most its share of the screen height (the message scrolls inside).
            var share = (area.Height - 2 * edge - gap * (cards.Count - 1)) / cards.Count;
            foreach (var c in Enumerable.Reverse(cards))
            {
                var h = Math.Min(c.WantedHeight, Math.Max(Scale(c, 150), share));
                if (c.Height != h) c.Height = h;
                var x = area.Right - c.Width - edge;
                if (s.Position == "top-right") { c.Location = new Point(x, y); y += h + gap; }
                else { y -= h; c.Location = new Point(x, y); y -= gap; }
            }
        }
        // Window style: every popup has the same fixed size; it only shrinks when the screen is shorter than that.
        foreach (var w in open.Where(w => w.Style != "stack")) FitToScreen(w, area, s);
    }

    /// Caps a popup window to the working area (a small laptop at 150 % can be shorter than 560 logical px).
    /// Never touches a maximised/minimised window or one the person resized themselves within the screen.
    private static void FitToScreen(NoticeWindow w, Rectangle area, NotifierSettings s)
    {
        if (w.WindowState != FormWindowState.Normal) return;
        var maxH = area.Height - Scale(w, 28);
        var maxW = area.Width - Scale(w, 28);
        if (w.Height <= maxH && w.Width <= maxW) return;
        var bottom = w.Bottom;
        w.Size = new Size(Math.Min(w.Width, maxW), Math.Min(w.Height, maxH));
        if (w.Bottom > area.Bottom) w.Top = Math.Max(area.Top, area.Bottom - w.Height);
        else if (s.Position != "top-right") w.Top = Math.Max(area.Top, bottom - w.Height);
        if (w.Right > area.Right) w.Left = Math.Max(area.Left, area.Right - w.Width);
    }

    private void UpdateMore()
    {
        var cards = open.Where(w => w.Style == "stack").ToList();
        for (var i = 0; i < cards.Count; i++) cards[i].SetMore(i == cards.Count - 1 ? waiting.Count : 0);
    }

    /// 1.5.0: the badge on the iPop taskbar button counts unread Needs action (from the Inbox), not open windows.
    private void Badge() => TaskbarBadge.Set(open.Where(w => w.Style != "stack"), actCount);

    private static int Scale(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96.0);
}
