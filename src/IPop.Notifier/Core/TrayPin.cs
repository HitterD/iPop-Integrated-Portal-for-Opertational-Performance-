using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// Windows 11 keeps a per-app record of tray icons under HKCU\Control Panel\NotifyIconSettings; IsPromoted = 1 means
/// "always show in the taskbar corner" (same switch as Settings → Personalisation → Taskbar → Other system tray icons).
/// Windows writes our record the first time the icon appears; we set it to shown once, after install, and never
/// again — if the person later hides it, that choice stays. Windows 10 has no such switch (the app shows a hint).
public static class TrayPin
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    /// True when the record existed and is now promoted (or already was).
    public static bool PromoteOnce(string markerFolder)
    {
        var marker = Path.Combine(markerFolder, "tray-pinned");
        if (File.Exists(marker)) return true;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return false; // Windows 10
            var exe = Environment.ProcessPath ?? "";
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key?.GetValue("ExecutablePath") is not string path || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (key.GetValue("IsPromoted") is not int p || p != 1) key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                Directory.CreateDirectory(markerFolder);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
}
