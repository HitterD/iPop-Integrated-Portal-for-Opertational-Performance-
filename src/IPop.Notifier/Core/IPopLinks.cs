using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// How the Notifier opens an iPop page without piling up browser tabs (1.4.5).
///   - Installed iPop app: web+ipop://open?to=/chat/… — the manifest's protocol_handlers route it to
///     /open.html inside the app, and launch_handler "focus-existing" reuses the open app window.
///   - Browser only: https://host/open.html?to=/chat/… — hands off to the open iPop tab, then closes.
public static class IPopLinks
{
    public const string Protocol = "web+ipop";

    /// A same-origin path ("/chat/x?y"), never another site; mirrors wwwroot/js/open-target.js.
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/notifications";
        path = path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        if (path.StartsWith("//") || path.StartsWith("/\\")) return "/dashboard";
        return path;
    }

    /// Pages the iPop web app routes itself. Downloads, the API, the Notifier connect page and the like stay
    /// plain browser opens: they are files or one-off flows, not a page for the open iPop tab.
    private static readonly string[] NotPages =
        ["/api/", "/hubs/", "/download/", "/downloads/", "/uploads/", "/onlyoffice/", "/livekit/", "/notifier/", "/connect", "/.well-known/"];

    public static bool IsIPopPage(Uri web, Uri u, out string path)
    {
        path = "";
        if (u.Scheme is not ("http" or "https")) return false;
        if (!string.Equals(u.Host, web.Host, StringComparison.OrdinalIgnoreCase) || u.Port != web.Port) return false;
        var p = u.AbsolutePath;
        if (NotPages.Any(n => p.StartsWith(n, StringComparison.OrdinalIgnoreCase) || p.Equals(n.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) return false;
        if (Path.HasExtension(p) && !p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;
        path = u.PathAndQuery + u.Fragment;
        return true;
    }

    public static bool IsHandoff(Uri u) => u.AbsolutePath.Equals("/open.html", StringComparison.OrdinalIgnoreCase);

    public static string HandoffUrl(Uri web, string path) =>
        new Uri(web, "open.html").ToString() + "?to=" + Uri.EscapeDataString(Normalize(path));

    public static string AppUrl(string path) => $"{Protocol}://open?to={Uri.EscapeDataString(Normalize(path))}";

    /// Chrome/Edge write HKCU\Software\Classes\web+ipop when an installed PWA declares the protocol, and
    /// remove it on uninstall. Test hook: IPOP_NOTIFIER_APP_PROTOCOL=0/1 forces the answer.
    public static bool AppProtocolRegistered()
    {
        var forced = Environment.GetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL");
        if (forced is "0") return false;
        if (forced is "1") return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Protocol}\shell\open\command");
            return key?.GetValue(null) is string cmd && cmd.Length > 0;
        }
        catch { return false; }
    }
}
