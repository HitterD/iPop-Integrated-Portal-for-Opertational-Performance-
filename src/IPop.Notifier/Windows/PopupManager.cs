using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Decides what happens to each notification and lays the windows out.
///  • "windows" (v2, default): one real window per notification, no limit (1.4.2: "20 popups = 20 windows"), never
///    minimised by the app. Cascaded 30 px from the screen corner, newest in front; when the next step would leave the
///    screen a new cascade column starts further left (then a new row), so every window stays visible and clickable.
///    All share the iPop taskbar button (same AppUserModelID) with a count badge. While held (meeting/full screen/
///    Focus/Busy) new windows open minimised and come out afterwards.
///  • "stack" (v1): glass cards stacked at the corner; as many as fit the screen height are shown, only the ones that
///    physically don't fit wait behind "+N more" and appear as cards close. While held they wait in the queue.
/// Nothing is ever dropped: every notification is also in the tray Inbox.
public sealed class PopupManager(IPopClient api, SettingsService settings, LocalState state, DeviceReporter device)
{
    private readonly List<NoticeWindow> open = [];
    private readonly Queue<Notice> waiting = new();
    /// Notifications that arrived while the PC was off: shown one at a time, oldest first ("1 of N pending").
    private readonly Queue<Notice> backlog = new();
    private NoticeWindow? backlogWindow;
    private int backlogTotal, backlogShown;
    public int BacklogCount => backlog.Count + (backlogWindow is null ? 0 : 1);
    private bool restyling;
    public event Action? CountChanged;
    public int OpenCount => open.Count + waiting.Count;

    public bool Held(Notice n, NotifierSettings s)
    {
        if (n.Urgent && s.UrgentBreaksThrough) return false;
        return s.DuringMeetings == "hold" && (Native.UserIsBusy() || device.IPopBusy);
    }

    /// Everything still unread from while the PC was off. Chat/ticket chat collapse to one toast per conversation;
    /// the rest open one window at a time; the next opens when that one is closed, read or answered.
    public void ShowBacklog(IReadOnlyList<Notice> items)
    {
        var s = settings.Effective(DateTime.UtcNow);
        var fresh = items.Where(n => !state.Shown.Contains(n.Id) && backlog.All(b => b.Id != n.Id)).ToList();
        foreach (var group in fresh.Where(IsConversation).GroupBy(n => n.TargetUrl ?? n.Id.ToString()))
        {
            var newest = group.MaxBy(n => n.At)!;
            var count = group.Count();
            foreach (var n in group) state.Shown.Add(n.Id);
            if (s.DeliveryFor(newest.Module, newest.EventType) == "off") continue;
            var title = count > 1 ? $"{count} unread · " + newest.Title : newest.Title;
            if (s.Popups != "on" || Held(newest, s)) { foreach (var n in group) Hold(n); continue; }
            Toasts.ShowNotice(newest with { Title = title }, api.Server);
        }
        foreach (var n in fresh.Where(n => !IsConversation(n))) backlog.Enqueue(n);
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
        if (s.Popups != "on" || Held(backlog.Peek(), s)) return;
        while (backlog.Count > 0)
        {
            var n = backlog.Dequeue();
            if (state.Shown.Contains(n.Id)) continue;
            state.Shown.Add(n.Id);
            if (s.DeliveryFor(n.Module, n.EventType) == "off") continue;
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

    /// Entry point for every notification (live or caught up).
    public void Show(Notice n)
    {
        if (state.Shown.Contains(n.Id)) return;
        state.Shown.Add(n.Id);
        if (state.LastShownAt is null || n.At > state.LastShownAt) state.LastShownAt = n.At;

        var s = settings.Effective(DateTime.UtcNow);
        var delivery = s.DeliveryFor(n.Module, n.EventType);
        var popupsOff = s.Popups != "on";
        if (delivery == "off") { state.Save(); return; }
        var held = Held(n, s);

        if (delivery == "toast" || n.Module == "chat")
        {
            if (popupsOff || held) Hold(n);
            else { Toasts.ShowNotice(n, api.Server); Sounds.Play(Sounds.For(n), s); }
            state.Save();
            return;
        }
        if (popupsOff && !(n.Urgent && s.UrgentBreaksThrough)) { Hold(n); state.Save(); return; }
        if (held) Hold(n);
        state.Save();

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

    private void Hold(Notice n)
    {
        if (!state.Held.Contains(n.Id)) state.Held.Add(n.Id);
    }

    private NoticeWindow Open(Notice n, NotifierSettings s, bool minimized, (int Index, int Total)? pending = null)
    {
        var w = new NoticeWindow(n, s.Style, api, pending);
        w.FormClosed += (_, _) => Closed(w);
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
        waiting.Clear();
        backlog.Clear();
        foreach (var w in open.ToList()) w.Close();
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
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
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
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var c = cards[0];
        return IPop.Application.Notifier.PopupLayout.StackFits(cards.Count + 1, area.Height, Scale(c, 14), Scale(c, 10), Scale(c, 150));
    }
    /// v1 stack: cards from the corner upwards (or downwards at the top), newest nearest the corner.
    /// v2 windows: one fixed size, capped to the screen.
    private void Layout()
    {
        var s = settings.Effective(DateTime.UtcNow);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
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

    private void Badge() => TaskbarBadge.Set(open.Where(w => w.Style != "stack"), open.Count(w => w.Style != "stack"));

    private static int Scale(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96.0);
}
