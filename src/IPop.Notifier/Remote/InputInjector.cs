using System.Runtime.InteropServices;

namespace IPop.Notifier.Remote;

/// Plays the helper's mouse and keyboard on this PC (SendInput). Coordinates arrive normalised 0..1 over the
/// shared monitor, so the helper's window size doesn't matter. Runs only while a session is active and in
/// "control" mode; the host decides, never the helper.
public sealed partial class InputInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint n, [In] INPUT[] inputs, int size);
    [LibraryImport("user32.dll")]
    private static partial ushort MapVirtualKeyW(uint code, uint mapType);

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010, MDOWN = 0x0020, MUP = 0x0040,
        WHEEL = 0x0800, HWHEEL = 0x1000, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
    private const uint KEYUP = 0x0002, UNICODE = 0x0004, EXTENDED = 0x0001, SCANCODE = 0x0008;

    /// The monitor being shared, in virtual-desktop pixels.
    public Rectangle Monitor { get; set; } = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    private readonly HashSet<ushort> down = [];
    /// Played / refused by Windows since the session started, and the last refusal code (5 = blocked: an admin
    /// window or the lock screen is in front, which needs the helper service).
    public int Played { get; private set; }
    public int Refused { get; private set; }
    public int LastError { get; private set; }

    public void Move(double x, double y) => Send(Mouse(x, y, MOVE));

    public void Button(double x, double y, int button, bool press)
    {
        var flag = (button, press) switch
        {
            (0, true) => LDOWN, (0, false) => LUP,
            (2, true) => RDOWN, (2, false) => RUP,
            (1, true) => MDOWN, (1, false) => MUP,
            _ => 0u,
        };
        if (flag != 0) Send(Mouse(x, y, MOVE | flag));
    }

    public void Wheel(double dx, double dy)
    {
        var list = new List<INPUT>();
        if (Math.Abs(dy) > 0.5) list.Add(new INPUT { type = INPUT_MOUSE, u = new() { mi = new MOUSEINPUT { mouseData = (int)Math.Clamp(-dy, -1200, 1200), dwFlags = WHEEL } } });
        if (Math.Abs(dx) > 0.5) list.Add(new INPUT { type = INPUT_MOUSE, u = new() { mi = new MOUSEINPUT { mouseData = (int)Math.Clamp(dx, -1200, 1200), dwFlags = HWHEEL } } });
        if (list.Count > 0) Count(SendInput((uint)list.Count, [.. list], Marshal.SizeOf<INPUT>()));
    }

    /// A key by Windows virtual-key code (layout independent shortcuts: Ctrl+C, Alt+Tab, F5…).
    public void Key(ushort vk, bool press, bool extended)
    {
        if (vk == 0) return;
        if (press) down.Add(vk); else down.Remove(vk);
        var scan = MapVirtualKeyW(vk, 0);
        var flags = (press ? 0 : KEYUP) | (extended ? EXTENDED : 0);
        Send(new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } });
    }

    /// Typed text that has no plain key on this layout (accents, emoji): sent as Unicode.
    public void Text(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text.Take(200))
        {
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE } } });
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE | KEYUP } } });
        }
        if (list.Count > 0) Count(SendInput((uint)list.Count, [.. list], Marshal.SizeOf<INPUT>()));
    }

    /// Lets go of every key the helper still holds (connection lost mid-shortcut must not leave Ctrl stuck).
    public void ReleaseAll()
    {
        foreach (var vk in down.ToList()) Key(vk, false, false);
        down.Clear();
    }

    private INPUT Mouse(double x, double y, uint flags)
    {
        var vs = SystemInformation.VirtualScreen;
        var px = Monitor.Left + Math.Clamp(x, 0, 1) * (Monitor.Width - 1);
        var py = Monitor.Top + Math.Clamp(y, 0, 1) * (Monitor.Height - 1);
        // Absolute coordinates over the whole virtual desktop, 0..65535.
        var ax = (int)Math.Round((px - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        var ay = (int)Math.Round((py - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        return new INPUT { type = INPUT_MOUSE, u = new() { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags | ABSOLUTE | VIRTUALDESK } } };
    }

    private void Send(INPUT i) => Count(SendInput(1, [i], Marshal.SizeOf<INPUT>()));

    private void Count(uint sent)
    {
        if (sent > 0) { Played++; return; }
        Refused++;
        LastError = Marshal.GetLastPInvokeError();
    }
}
