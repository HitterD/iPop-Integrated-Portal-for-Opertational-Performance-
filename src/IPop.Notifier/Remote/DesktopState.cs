using System.Runtime.InteropServices;
using System.Text;

namespace IPop.Notifier.Remote;

/// Win32 facts about the user's desktop during remote help (split out of RemoteHost, 1.8.0): which desktop has the
/// input, what is in front, and why mouse/keyboard may not reach the PC.
internal static class DesktopState
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(nint h, int index, StringBuilder buf, int len, out int needed);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

    private static string DeskName(nint h)
    {
        if (h == 0) return "";
        var sb = new StringBuilder(64);
        return GetUserObjectInformationW(h, 2, sb, sb.Capacity * 2, out _) ? sb.ToString() : "?";
    }

    /// Process name of the window in front ("" when none or unreadable).
    public static string Foreground()
    {
        try { GetWindowThreadProcessId(GetForegroundWindow(), out var p); return System.Diagnostics.Process.GetProcessById((int)p).ProcessName; }
        catch { return ""; }
    }

    /// Facts that decide whether Windows lets this app play input: integrity level, which session is on the
    /// console, and whether our thread sits on the input desktop.
    public static string Environment()
    {
        var il = "?";
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            il = id.Groups?.FirstOrDefault(g => g.Value.StartsWith("S-1-16-"))?.Value switch
            {
                "S-1-16-4096" => "low", "S-1-16-8192" => "medium", "S-1-16-8448" => "medium+", "S-1-16-12288" => "high", "S-1-16-16384" => "system", var v => v ?? "?",
            };
        }
        catch { }
        var mySession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return $"il={il} session={mySession} console={WTSGetActiveConsoleSessionId()} threadDesk={DeskName(GetThreadDesktop(GetCurrentThreadId()))} fgHwnd={GetForegroundWindow()}";
    }

    /// Why mouse/keyboard may not reach this PC right now:
    ///   "ok"        the person's desktop is in front
    ///   "locked"    lock screen, sign-in, UAC or Ctrl+Alt+Del (a secure desktop)
    ///   "rdp-idle"  this Windows session is a Remote Desktop session that nobody is looking at (window minimised
    ///               or disconnected) — Windows stops drawing it and refuses input
    ///   "rdp"       a Remote Desktop session that is being viewed (works, but frame rate is limited by RDP)
    ///   "idle"      no window has the focus (screen off / nothing in front)
    public static string Reason(string desk, string fg)
    {
        const int SM_REMOTESESSION = 0x1000;
        var rdp = GetSystemMetrics(SM_REMOTESESSION) != 0;
        if (desk != "Default") return "locked";
        if (GetForegroundWindow() == 0 || fg == "Idle") return rdp ? "rdp-idle" : "idle";
        return rdp ? "rdp" : "ok";
    }

    /// "Default" while the person's desktop is in front; "Winlogon"/"" when the PC is locked or a UAC prompt is up.
    public static string InputDesktop()
    {
        var h = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (h == 0) return "";                     // access denied = a secure desktop is in front
        try { return DeskName(h) is "?" ? "" : DeskName(h); }
        finally { CloseDesktop(h); }
    }
}
