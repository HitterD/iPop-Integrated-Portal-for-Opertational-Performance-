using System.Runtime.InteropServices;

namespace IPop.Notifier.Windows;

/// Count badge on the iPop taskbar button (ITaskbarList3 overlay icon), as in mockup v2.
public static class TaskbarBadge
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint hwndTab, nint hwndMdi);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarSetImageList(nint hwnd, nint himl);
        void SetOverlayIcon(nint hwnd, nint hIcon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(nint hwnd, nint clip);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass;

    private static ITaskbarList3? list;
    private static readonly Dictionary<int, Icon> Cache = [];

    public static void Set(IEnumerable<Form> windows, int count)
    {
        try
        {
            list ??= (ITaskbarList3)new TaskbarListClass();
            list.HrInit();
            var icon = count > 0 ? Badge(count) : null;
            foreach (var w in windows)
                if (w.IsHandleCreated && w.ShowInTaskbar)
                    list.SetOverlayIcon(w.Handle, icon?.Handle ?? 0, count > 0 ? $"{count} notifications" : "");
        }
        catch { /* Explorer restarting; next update retries */ }
    }

    private static Icon Badge(int count)
    {
        var key = Math.Min(count, 100);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var red = new SolidBrush(Color.FromArgb(255, 59, 48));
            g.FillEllipse(red, 0, 0, 31, 31);
            var text = key > 99 ? "99+" : key.ToString();
            using var font = new Font("Segoe UI", text.Length > 2 ? 10 : text.Length > 1 ? 13 : 16, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.White, (32 - sz.Width) / 2, (32 - sz.Height) / 2 + 1);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Core.Native.DestroyIcon(h);
        Cache[key] = icon;
        return icon;
    }
}
