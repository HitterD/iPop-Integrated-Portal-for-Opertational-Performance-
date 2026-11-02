using System.Diagnostics;
using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// Per-user Windows integration (HKCU only, so no admin rights): start with Windows, the ipop-notifier:// link
/// scheme, and opening company NAS files in File Explorer.
public static class Integration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string RunName = "iPop Notifier" + (AppInfo.Flavour.Length > 0 ? " " + AppInfo.Flavour : "");

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "iPopNotifier.exe");

    /// Debug QA runs (IPOP_NOTIFIER_QA_DIR) never write to this PC's registry.
    private static bool QaRun =>
#if DEBUG
        Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is { Length: > 0 };
#else
        false;
#endif

    public static void SetStartWithWindows(bool on)
    {
        if (QaRun) return;
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
        if (QaRun) return;
        foreach (var scheme in AppInfo.Flavour.Length > 0 ? new[] { AppInfo.UrlScheme } : new[] { AppInfo.UrlScheme, AppInfo.FilesScheme })
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                key.SetValue("", "URL:iPop Notifier");
                key.SetValue("URL Protocol", "");
                using var icon = key.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{ExePath}\",0");
                using var cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
            }
            catch { }
        }
    }

    public static void UnregisterUrlScheme()
    {
        if (QaRun) return;
        foreach (var scheme in AppInfo.Flavour.Length > 0 ? new[] { AppInfo.UrlScheme } : new[] { AppInfo.UrlScheme, AppInfo.FilesScheme })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{scheme}", false); } catch { }
        }
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

    /// Short system sound for "someone asks to remote this PC" (the prompt must not be missed).
    public static void Chime() { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }

    /// Opens an iPop notification target in the browser. A ticket chat for the assigned agent
    /// (/idesk/tickets/{id}) lives only in iDesk: iPop answers the iDesk link (signed in through "Sign in with
    /// iPop"), so it opens on the iDesk site, never as an iPop page.
    public static async Task OpenTargetAsync(IPopClient api, string? path)
    {
        path = string.IsNullOrEmpty(path) ? "/notifications" : path;
        const string idesk = "/idesk/tickets/";
        if (path.StartsWith(idesk) && path.Length >= idesk.Length + 36 && Guid.TryParse(path.AsSpan(idesk.Length, 36), out var id))
        {
            var link = (await api.GetJsonAsync($"api/tickets/idesk/{id}/link"))?["url"]?.GetValue<string>();
            if (link is { Length: > 0 }) { OpenUrl(link); return; }
            Log.Warn("open", "no iDesk link for " + id);
            return;
        }
        OpenIPopPage(api, path);
    }

    /// An iPop page never becomes one more browser tab per message. Installed iPop app (Chrome/Edge register
    /// its web+ipop: protocol in HKCU) → the app window is focused and navigates. Otherwise /open.html hands
    /// the page to the iPop tab that is already open and closes itself (no open tab: it loads the page).
    public static void OpenIPopPage(IPopClient api, string path)
    {
        var target = IPopLinks.Normalize(path);
        if (IPopLinks.AppProtocolRegistered())
        {
            try
            {
                Process.Start(new ProcessStartInfo(IPopLinks.AppUrl(target)) { UseShellExecute = true });
                return;
            }
            catch (Exception ex) { Log.Warn("open", "app protocol failed: " + ex.Message); }
        }
        OpenUrl(IPopLinks.HandoffUrl(api.Web, target));
    }

    /// The signed-in client, so any link to the iPop site (a URL inside a message, a meeting link) also reuses
    /// the open iPop tab/app instead of opening another tab.
    public static IPopClient? Api { get; set; }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http" && u.Scheme != "mailto")) return;
        if (Api is { } api && IPopLinks.IsIPopPage(api.Web, u, out var path) && !IPopLinks.IsHandoff(u))
        {
            OpenIPopPage(api, path);
            return;
        }
#if DEBUG
        // QA runs: hand the URL to the test browser (it opens each line as a new tab) instead of the PC's own browser.
        if (QaRun && Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_OPEN_LOG") is { Length: > 0 } openLog)
        {
            try { File.AppendAllText(openLog, u + Environment.NewLine); } catch { }
            return;
        }
#endif
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}
