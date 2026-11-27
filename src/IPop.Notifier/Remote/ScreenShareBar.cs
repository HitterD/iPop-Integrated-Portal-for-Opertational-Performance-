using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IPop.Notifier.Core;

namespace IPop.Notifier.Remote;

/// Hotfix 1.8.2: Chromium shows "notifier.ipop is sharing your screen · [Stop sharing] [Hide]" while the remote host
/// page captures. It is part of the shared picture (so ICT sees it) and "Stop sharing" ends the picture without
/// ending the session. WebView2 has no switch for it, so the bar window is hidden once it appears. Only windows of
/// the Notifier's own msedgewebview2 children with exactly that title are touched. The user keeps the green frame
/// and "ICT is helping you" (End session) as the visible signal and control.
internal static class ScreenShareBar
{
    private const string Suffix = " is sharing your screen.";

    /// Looks a few times over the next seconds (the bar appears after capture starts, and again after a rejoin).
    public static void HideSoon()
    {
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(i < 4 ? 250 : 1000);
                try { if (HideNow() > 0) Log.Info("remote", "screen-share bar hidden"); }
                catch (Exception ex) { Log.Warn("remote", "screen-share bar: " + ex.GetType().Name); return; }
            }
        });
    }

    /// Hides every visible bar owned by one of this app's WebView2 processes. Returns how many it hid.
    internal static int HideNow()
    {
        var mine = OwnWebViewProcesses();
        var hidden = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!mine.Contains((int)pid)) return true;
            var title = new StringBuilder(256);
            GetWindowTextW(h, title, title.Capacity);
            if (IsBarTitle(title.ToString())) { ShowWindow(h, 0); hidden++; }
            return true;
        }, 0);
        return hidden;
    }

    internal static bool IsBarTitle(string title) =>
        title.EndsWith(Suffix, StringComparison.Ordinal) && title.Contains("notifier.ipop", StringComparison.OrdinalIgnoreCase);

    /// msedgewebview2.exe processes whose parent is this Notifier (browser processes; the bar lives there).
    private static HashSet<int> OwnWebViewProcesses()
    {
        var me = Environment.ProcessId;
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("msedgewebview2"))
        {
            try { if (ParentOf(p.Id) == me) set.Add(p.Id); } catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    private static int ParentOf(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return -1;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : -1;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION { public nint Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }

    private delegate bool EnumProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, StringBuilder s, int n);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint h, int cls, ref PROCESS_BASIC_INFORMATION info, int len, out int ret);
}
