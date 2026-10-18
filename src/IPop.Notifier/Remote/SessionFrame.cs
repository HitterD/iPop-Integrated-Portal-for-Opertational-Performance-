using System.Runtime.InteropServices;

namespace IPop.Notifier.Remote;

/// The thin green frame round the user's screen while ICT is connected (mockup remote §1). Click-through, always on
/// top, not on the taskbar, and excluded from screen capture so the helper doesn't see it in the stream.
public sealed partial class SessionFrame : Form
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(nint hWnd, uint affinity);
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly string label;
    private float pulse;
    private readonly System.Windows.Forms.Timer anim = new() { Interval = 60 };

    public SessionFrame(Rectangle monitor, string label)
    {
        this.label = label;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = monitor;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        anim.Tick += (_, _) => { pulse = (pulse + 0.05f) % (2 * MathF.PI); Invalidate(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        anim.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // TransparencyKey windows have no partial alpha: the pulse is between two solid greens.
        var t = (MathF.Sin(pulse) + 1) / 2;
        var w = Math.Max(4, (int)(4 * DeviceDpi / 96.0));
        using var pen = new Pen(Color.FromArgb(255, (int)(48 - 10 * t), (int)(209 - 50 * t), (int)(88 - 20 * t)), w);
        g.DrawRectangle(pen, w / 2, w / 2, Width - w, Height - w);
        // Tag at the top centre: "ICT is connected · Bagas".
        using var font = new Font("Segoe UI Semibold", 12f * DeviceDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        var size = g.MeasureString(label, font);
        var tw = (int)size.Width + 24; var th = (int)size.Height + 8;
        var rect = new Rectangle((Width - tw) / 2, 0, tw, th);
        using var brush = new SolidBrush(Color.FromArgb(48, 158, 91));
        using var path = Rounded(rect, 9);
        g.FillPath(brush, path);
        g.DrawString(label, font, Brushes.White, rect.X + 12, rect.Y + 3);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddLine(r.Left, r.Top, r.Right, r.Top);
        p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        p.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }
}
