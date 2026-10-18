using System.Diagnostics;
using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// Per-user Windows integration (HKCU only, so no admin rights): start with Windows, the ipop-notifier:// link
/// scheme, and opening company NAS files in File Explorer.
public static class Integration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "iPop Notifier";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "iPopNotifier.exe");

    public static void SetStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunName, $"\"{ExePath}\" --background");
            else key.DeleteValue(RunName, false);
        }
        catch { }
    }

    public static void RegisterUrlScheme()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{AppInfo.UrlScheme}");
            key.SetValue("", "URL:iPop Notifier");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{ExePath}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch { }
    }

    public static void UnregisterUrlScheme()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{AppInfo.UrlScheme}", false); } catch { }
    }

    /// The five company Synology NAS (same keys as the server's Synology:Servers). Nothing else may be opened.
    public static readonly IReadOnlyDictionary<string, string> Nas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["spj"] = "192.168.2.16",
        ["krw"] = "10.12.2.6",
        ["jtb"] = "10.11.2.6",
        ["smg"] = "10.14.2.16",
        ["ict"] = "192.168.2.17",
    };

    /// "/sja/SJA SPJ/ICT/a.xlsx" on "spj" → \\192.168.2.16\sja\SJA SPJ\ICT\a.xlsx. Null when the NAS is unknown
    /// or the path tries to climb out ("..") or carries anything that isn't a plain path.
    public static string? UncPath(string nas, string path)
    {
        if (!Nas.TryGetValue(nas, out var host)) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return @"\\" + host + @"\" + string.Join('\\', parts);
    }

    /// A UNC path is allowed only when its host is one of the five NAS.
    public static bool IsCompanyNas(string unc)
    {
        if (!unc.StartsWith(@"\\")) return false;
        var host = unc[2..].Split('\\', 2)[0];
        return Nas.Values.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    /// Explorer opens the folder with the file selected (or the folder itself).
    public static bool OpenInExplorer(string unc)
    {
        if (!IsCompanyNas(unc) || unc.Contains("..")) return false;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (Directory.Exists(unc)) psi.ArgumentList.Add(unc);
            else psi.Arguments = $"/select,\"{unc}\"";
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http" && u.Scheme != "mailto")) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}
