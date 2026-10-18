using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Decides what happens to each notification and lays the windows out.
///  • "windows" (v2, default): one real window per notification, cascaded 28 px from the screen corner, newest in
///    front; all share the iPop taskbar button (same AppUserModelID) with a count badge. Beyond MaxVisible the oldest
///    minimise into the group. While held (meeting/full screen/Focus/Busy) new windows open minimised.
///  • "stack" (v1): glass cards stacked at the corner; MaxVisible shown, the rest wait behind "+N more" and appear as
///    cards close. While held they wait in the queue.
/// Nothing is ever dropped: every notification is also in the tray Inbox.
public sealed class PopupManager(IPopClient api, SettingsService settings, LocalState state, DeviceReporter device)
{
    private readonly List<NoticeWindow> open = [];
    private readonly Queue<Notice> waiting = new();
    private bool restyling;
    public event Action? CountChanged;
    public int OpenCount => open.Count + waiting.Count;

    public bool Held(Notice n, NotifierSettings s)
    {
        if (n.Urgent && s.UrgentBreaksThrough) return false;
        return s.DuringMeetings == "hold" && (Native.UserIsBusy() || device.IPopBusy);
    }

    /// Entry point for every notification (live or caught up).
    public void Show(Notice n)
    {
        if (state.Shown.Contains(n.Id)) return;
        state.Shown.Add(n.Id);
        if (state.LastShownAt is null || n.At > state.LastShownAt) state.LastShownAt = n.At;

        var s = settings.Effective(DateTime.UtcNow);
        var delivery = s.DeliveryFor(n.Module);
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
            if (held || open.Count(w => w.Style == "stack") >= s.MaxVisible) { waiting.Enqueue(n); UpdateMore(); CountChanged?.Invoke(); return; }
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

    private void Open(Notice n, NotifierSettings s, bool minimized)
    {
        var w = new NoticeWindow(n, s.Style, api);
        w.FormClosed += (_, _) => Closed(w);
        w.HeightChanged += _ => Layout();
        w.Activated += (_, _) => Front(w);
        open.Add(w);
        Place(w, s);
        if (minimized) w.WindowState = FormWindowState.Minimized;
        w.Show();
        if (s.Style != "stack")
        {
            // More than MaxVisible windows on screen: the oldest minimise into the taskbar group.
            var visible = open.Where(x => x.Style != "stack" && x.WindowState != FormWindowState.Minimized).ToList();
            foreach (var old in visible.Take(Math.Max(0, visible.Count - Math.Max(s.MaxVisible, 1))))
                old.WindowState = FormWindowState.Minimized;
        }
        Badge();
        UpdateMore();
        CountChanged?.Invoke();
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
        while (waiting.Count > 0 && open.Count(x => x.Style == "stack") < s.MaxVisible && !Held(waiting.Peek(), s))
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
        while (waiting.Count > 0 && open.Count(x => x.Style == "stack") < s.MaxVisible)
            Open(waiting.Dequeue(), s with { Style = "stack" }, minimized: false);
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
            if (s.Style == "stack" && open.Count >= s.MaxVisible) waiting.Enqueue(n);
            else Open(n, s, minimized: false);
        }
        Layout();
    }

    private void Place(NoticeWindow w, NotifierSettings s)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var gap = Scale(w, 14);
        if (w.Style == "stack") { Layout(); return; }
        // Cascade: each new window 28 px left/down of the last, wrapping after 8 (as in the mockup).
        var i = open.Count(x => x.Style != "stack") - 1;
        var step = Scale(w, 28) * (i % 8);
        var x = area.Right - w.Width - gap - step;
        var y = s.Position == "top-right" ? area.Top + gap + step : area.Bottom - w.Height - gap - Scale(w, 160) + step;
        w.Location = new Point(Math.Max(area.Left, x), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - w.Height)));
    }

    /// v1 stack: cards from the corner upwards (or downwards at the top), newest nearest the corner.
    private void Layout()
    {
        var s = settings.Effective(DateTime.UtcNow);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var cards = open.Where(w => w.Style == "stack").ToList();
        if (cards.Count == 0) return;
        var gap = Scale(cards[0], 10);
        var edge = Scale(cards[0], 14);
        var y = s.Position == "top-right" ? area.Top + edge : area.Bottom - edge;
        foreach (var c in Enumerable.Reverse(cards))
        {
            var h = c.WantedHeight;
            if (c.Height != h) c.Height = h;
            var x = area.Right - c.Width - edge;
            if (s.Position == "top-right") { c.Location = new Point(x, y); y += h + gap; }
            else { y -= h; c.Location = new Point(x, y); y -= gap; }
        }
        foreach (var w in open.Where(w => w.Style != "stack" && w.Height != w.WantedHeight))
        {
            var bottom = w.Bottom;
            w.Height = w.WantedHeight;
            if (w.Bottom > area.Bottom) w.Top = Math.Max(area.Top, area.Bottom - w.Height);
            else if (s.Position != "top-right") w.Top = Math.Max(area.Top, bottom - w.Height);
        }
    }

    private void UpdateMore()
    {
        var cards = open.Where(w => w.Style == "stack").ToList();
        for (var i = 0; i < cards.Count; i++) cards[i].SetMore(i == cards.Count - 1 ? waiting.Count : 0);
    }

    private void Badge() => TaskbarBadge.Set(open.Where(w => w.Style != "stack"), open.Count(w => w.Style != "stack"));

    private static int Scale(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96.0);
}
