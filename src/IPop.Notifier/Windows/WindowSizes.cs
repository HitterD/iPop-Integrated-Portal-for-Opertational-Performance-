using System.Text.Json;
using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// Window sizes the person dragged to (popup, inbox, settings, devices), kept per PC in sizes.json.
/// Sizes are logical (96 dpi) so they survive a DPI change.
public static class WindowSizes
{
    private static readonly string FilePath = Path.Combine(SessionStore.Folder, "sizes.json");
    private static Dictionary<string, int[]>? cache;

    private static Dictionary<string, int[]> All()
    {
        if (cache is not null) return cache;
        try { cache = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(FilePath)) ?? []; }
        catch { cache = []; }
        return cache;
    }

    public static Size Get(string key, Size fallback)
    {
        if (!All().TryGetValue(key, out var v) || v.Length != 2) return fallback;
        // Never smaller than 70 % of the default or bigger than a 4K screen.
        return new Size(Math.Clamp(v[0], fallback.Width * 7 / 10, 3840), Math.Clamp(v[1], fallback.Height * 7 / 10, 2160));
    }

    public static void Remember(string key, Form form)
    {
        if (form.WindowState != FormWindowState.Normal) return;
        var scale = form.DeviceDpi / 96.0;
        All()[key] = [(int)Math.Round(form.ClientSize.Width / scale), (int)Math.Round(form.ClientSize.Height / scale)];
        try
        {
            Directory.CreateDirectory(SessionStore.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cache));
        }
        catch { }
    }
}
