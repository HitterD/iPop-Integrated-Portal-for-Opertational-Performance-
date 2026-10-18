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

    /// Set to a key ("popup", "inbox"…) to let the person resize the window by its edges and keep that size.
    protected string? SizeKey { get; set; }
    private const int Grip = 6;

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
        BackColor = Theme.IsDark ? Color.FromArgb(32, 32, 34) : Color.FromArgb(250, 250, 252);
        ClientSize = size;
        Controls.Add(View);
    }

    /// Notification windows must not take the keyboard from what the user is typing (mockup v2 behaviour).
    protected override bool ShowWithoutActivation => noActivate;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // A thin frame around the page is the resize grip (the WebView would swallow the edge otherwise).
        if (SizeKey is null) return;
        Padding = new Padding((int)Math.Round(Grip * DeviceDpi / 96.0));
        MinimumSize = new Size(Width * 7 / 10, Height * 6 / 10);
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
        if (m.Msg != WM_NCHITTEST || SizeKey is null || WindowState != FormWindowState.Normal) return;
        var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        var g = (int)Math.Round(Grip * DeviceDpi / 96.0);
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
            const int WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000, CS_DROPSHADOW = 0x20000;
            cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU; // taskbar click minimises/restores a borderless window
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
                Send(new { type = "theme", dark = Theme.IsDark });
                foreach (var m in pending) WebHost.Post(View, m);
                pending.Clear();
                OnReady();
                break;
            case "drag": Native.BeginDrag(Handle); break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "close": Close(); break;
            case "openUrl": Integration.OpenUrl(msg?["url"]?.GetValue<string>() ?? ""); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) View.Dispose();
        base.Dispose(disposing);
    }
}

public static class Theme
{
    public static bool IsDark
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
