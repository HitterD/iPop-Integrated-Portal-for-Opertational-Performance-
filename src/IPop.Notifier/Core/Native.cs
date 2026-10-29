using System.Runtime.InteropServices;

namespace IPop.Notifier.Core;

internal static partial class Native
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// QUNS_*: 1 not present, 2 busy (full-screen), 3 running D3D full screen, 4 presentation mode,
    /// 5 accepts notifications, 6 quiet time, 7 app (UWP) full screen.
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInput info);

    /// Time since the last keyboard or mouse input in this Windows session.
    public static TimeSpan IdleTime()
    {
        var li = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.Time));
    }

    public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// True while the user shouldn't be interrupted: full-screen app/game, presenting, or Windows Focus/quiet time.
    public static bool UserIsBusy()
    {
        try { return SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4 or 6 or 7; }
        catch { return false; }
    }

    public static void RoundCorners(nint hwnd)
    {
        var round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); } catch { }
    }

    public static void DarkTitle(nint hwnd, bool dark)
    {
        var v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    public const int WM_GETMINMAXINFO = 0x24;

    /// A borderless window maximises over the taskbar unless told otherwise: the maximised size/position become the
    /// working area of the monitor the window is on (MINMAXINFO positions are relative to that monitor).
    public static void FitMaximizedToWorkArea(nint hwnd, nint lParam)
    {
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (mon == 0 || !GetMonitorInfo(mon, ref mi)) return;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition = new Point(mi.Work.Left - mi.Monitor.Left, mi.Work.Top - mi.Monitor.Top);
        mmi.MaxSize = new Point(mi.Work.Right - mi.Work.Left, mi.Work.Bottom - mi.Work.Top);
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    /// Lets the HTML title bar move a borderless window.
    public static void BeginDrag(nint hwnd)
    {
        ReleaseCapture();
        SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }
}
