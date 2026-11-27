namespace IPop.Notifier.Remote;

/// The user's monitors for the viewer's Monitor picker. Index 0..n-1 = Screen.AllScreens with the main one first;
/// -1 = "All" (the whole virtual desktop). Rectangles are virtual-desktop pixels.
/// Hotfix 1.8.1: WebView2 shares one whole screen (the 1.8.0 whole-desktop crop never started on two monitors), so the
/// picker is read-only for now and input goes to the main monitor, which is the one shared ("Screen 1").
internal static class Monitors
{
    public const int All = -1;

    public static Screen[] List() => [.. Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top)];

    public static Rectangle Bounds(int index)
    {
        var list = List();
        return index == All || list.Length == 0 ? SystemInformation.VirtualScreen
            : list[Math.Clamp(index, 0, list.Length - 1)].Bounds;
    }

    /// Crop of the captured virtual desktop; null = the whole picture.
    public static object? Crop(int index)
    {
        if (index == All) return null;
        var vs = SystemInformation.VirtualScreen;
        var b = Bounds(index);
        return new { x = b.Left - vs.Left, y = b.Top - vs.Top, w = b.Width, h = b.Height };
    }

    public static object Virtual() { var vs = SystemInformation.VirtualScreen; return new { w = vs.Width, h = vs.Height }; }

    /// What the viewer shows: "Monitor 1 · Main 1920×1080", …, "All combined" (only with two or more).
    public static object Describe(int current, bool switchable)
    {
        var list = List();
        var items = list.Select((s, i) => (object)new { index = i, name = $"Monitor {i + 1}" + (s.Primary ? " · Main" : ""), size = $"{s.Bounds.Width}×{s.Bounds.Height}" }).ToList();
        if (list.Length > 1 && switchable) items.Add(new { index = All, name = "All", size = "combined" });
        return new { t = "monitors", current, count = list.Length, switchable, items };
    }
}
