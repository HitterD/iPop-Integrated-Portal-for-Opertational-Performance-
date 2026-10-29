using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IPop.Notifier.Windows;

/// A borderless window whose whole UI is one HTML page (title bar included, as in the mockups). Rounded corners and
/// the dark title colour come from Windows 11 DWM. Pages talk to the app with window.chrome.webview.postMessage.
public class HtmlWindow : Form
{
    protected readonly WebView2 View = new() { Dock = DockStyle.Fill };
    private readonly string page;
    private readonly bool noActivate;
    private bool ready;
    private readonly List<object> pending = [];
    private FormWindowState lastState = FormWindowState.Normal;

    /// Set to a key ("inbox", "settings"…) to keep the size the person dragged to (implies <see cref="Resizable"/>).
    protected string? SizeKey { get; set; }
    /// Edges resize the window (borderless, so the app answers WM_NCHITTEST itself).
    protected bool Resizable { get; set; }
    /// □ in the title bar, double-click on the header and Win+Up maximise to the working area (never over the taskbar).
    protected bool Maximizable { get; set; }
    /// Smallest size when resizing, logical px (null: 70 % × 60 % of the opening size).
    protected Size? MinLogicalSize { get; set; }
    private const int Grip = 6;
    private bool CanResize => Resizable || SizeKey is not null;

    public HtmlWindow(string page, Size size, bool noActivate = false, bool showInTaskbar = true)
    {
        this.page = page;
        this.noActivate = noActivate;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = showInTaskbar;
        Text = "iPop Notifier";
        Icon = AppIcon.Icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        ClientSize = size;
        Controls.Add(View);
        Theme.Changed += OnThemeChanged;
    }

    /// Notification windows must not take the keyboard from what the user is typing (mockup v2 behaviour).
    protected override bool ShowWithoutActivation => noActivate;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // A thin frame around the page is the resize grip (the WebView would swallow the edge otherwise).
        if (!CanResize) return;
        ApplyGrip();
        var min = MinLogicalSize is { } m ? new Size(Scale(m.Width), Scale(m.Height)) : new Size(Width * 7 / 10, Height * 6 / 10);
        // Never bigger than the opening size (already capped to the screen: a small laptop at 150 % can be shorter
        // than 560 logical px).
        MinimumSize = new Size(Math.Min(min.Width, Width), Math.Min(min.Height, Height));
    }

    private void ApplyGrip()
    {
        if (!CanResize) return;
        var g = WindowState == FormWindowState.Maximized ? 0 : Scale(Grip);
        if (Padding.All != g) Padding = new Padding(g);
    }

    /// Last non-minimised state (a minimised maximised window comes back maximised).
    public bool Maximized { get; private set; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == lastState) return;
        lastState = WindowState;
        ApplyGrip();
        if (WindowState == FormWindowState.Minimized) return;
        var max = WindowState == FormWindowState.Maximized;
        if (max == Maximized) return;
        Maximized = max;
        Send(new { type = "winstate", max });
        OnWindowStateChanged();
    }

    protected virtual void OnWindowStateChanged() { }

    public void ToggleMaximize()
    {
        if (!Maximizable) return;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (SizeKey is not null) WindowSizes.Remember(SizeKey, this);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        base.WndProc(ref m);
        if (m.Msg == Native.WM_GETMINMAXINFO) { Native.FitMaximizedToWorkArea(Handle, m.LParam); return; }
        if (m.Msg != WM_NCHITTEST || !CanResize || WindowState != FormWindowState.Normal) return;
        var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        var g = Scale(Grip);
        bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
        // HTLEFT 10, HTRIGHT 11, HTTOP 12, HTTOPLEFT 13, HTTOPRIGHT 14, HTBOTTOM 15, HTBOTTOMLEFT 16, HTBOTTOMRIGHT 17
        var hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
        if (hit != 0) m.Result = hit; // the edge hit codes alone give the resize cursor + drag (no visible frame)
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            const int WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_SYSMENU = 0x80000, CS_DROPSHADOW = 0x20000;
            cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU; // taskbar click minimises/restores a borderless window
            if (Maximizable) cp.Style |= WS_MAXIMIZEBOX; // Win+Up and the taskbar menu can maximise too
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
        Native.DarkTitle(Handle, Theme.IsDark);
        try
        {
            await WebHost.InitAsync(View, page, (type, msg) => BeginInvoke(() => HandleMessage(type, msg)));
        }
        catch (Exception ex)
        {
            Controls.Clear();
            Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "iPop Notifier needs Microsoft Edge WebView2.\n" + ex.Message });
        }
    }

    /// Settings → Appearance / Text size, or Windows switched light/dark: every open window follows at once.
    private void OnThemeChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(OnThemeChanged); return; }
        BackColor = Theme.Surface;
        Native.DarkTitle(Handle, Theme.IsDark);
        if (ready) SendTheme();
    }

    private void SendTheme() => WebHost.Post(View, new { type = "theme", dark = Theme.IsDark, large = Theme.LargeText, mode = Theme.Mode });

    /// Messages sent before the page said "ready" are queued.
    public void Send(object message)
    {
        if (!ready) { pending.Add(message); return; }
        WebHost.Post(View, message);
    }

    protected virtual void OnReady() { }

    protected virtual void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "ready":
                ready = true;
                SendTheme();
                WebHost.Post(View, new { type = "winstate", max = WindowState == FormWindowState.Maximized, canMax = Maximizable });
                foreach (var m in pending) WebHost.Post(View, m);
                pending.Clear();
                OnReady();
                break;
            case "drag": if (WindowState == FormWindowState.Normal) Native.BeginDrag(Handle); break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "maximize": ToggleMaximize(); break;
            case "close": Close(); break;
            case "openUrl": Integration.OpenUrl(msg?["url"]?.GetValue<string>() ?? ""); break;
        }
    }

    protected int Scale(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= OnThemeChanged; View.Dispose(); }
        base.Dispose(disposing);
    }
}

/// Light/Dark for every Notifier window: Settings → General → Appearance ("system" follows Windows, live).
public static class Theme
{
    private static string mode = "system";
    private static bool large;
    private static bool? lastDark;

    /// Appearance or Text size changed in Settings, or Windows switched light/dark while the mode is "system".
    public static event Action? Changed;

    /// Settings → "Text size: Large": bigger type and buttons in every window (set by the app when settings load/change).
    public static bool LargeText
    {
        get => large;
        set { if (large == value) return; large = value; Changed?.Invoke(); }
    }

    /// "system" | "light" | "dark".
    public static string Mode
    {
        get => mode;
        set
        {
            var v = value is "light" or "dark" ? value : "system";
            if (v == mode) return;
            mode = v;
            lastDark = IsDark;
            Changed?.Invoke();
        }
    }

    public static bool IsDark => Resolve(mode, SystemIsDark);

    /// The page surface colour (Ui/app.css --surface), also the form background behind the WebView.
    public static Color Surface => IsDark ? Color.FromArgb(28, 28, 30) : Color.White;

    public static bool Resolve(string? mode, bool systemDark) => mode switch { "dark" => true, "light" => false, _ => systemDark };

    /// Follows Windows' own light/dark switch while the mode is "system" (call once, from the UI thread).
    public static void FollowWindows()
    {
        lastDark = IsDark;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            var now = IsDark;
            if (now == lastDark) return;
            lastDark = now;
            Changed?.Invoke();
        };
    }

    private static bool SystemIsDark
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }
}

public static class AppIcon
{
    private static Icon? icon;
    public static Icon Icon => icon ??= File.Exists(Path.Combine(AppInfo.AssetsDir, "ipop.ico"))
        ? new Icon(Path.Combine(AppInfo.AssetsDir, "ipop.ico"))
        : SystemIcons.Application;

    private static Icon? remote;
    /// Remote help running: the iPop icon with a green ring.
    public static Icon Remote
    {
        get
        {
            if (remote is not null) return remote;
            using var bmp = new Icon(Icon, 32, 32).ToBitmap();
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.FromArgb(48, 209, 88), 4);
                g.DrawEllipse(pen, 2, 2, 28, 28);
            }
            var h = bmp.GetHicon();
            remote = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            return remote;
        }
    }

    /// Tray icon with a small red dot when there is something unread.
    public static Icon Tray(bool unread)
    {
        if (!unread) return Icon;
        using var bmp = new Icon(Icon, 32, 32).ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(Brushes.White, 17, -1, 16, 16);
            using var red = new SolidBrush(Color.FromArgb(255, 59, 48));
            g.FillEllipse(red, 19, 1, 12, 12);
        }
        var h = bmp.GetHicon();
        var result = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return result;
    }
}
