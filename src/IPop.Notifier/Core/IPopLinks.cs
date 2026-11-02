using Microsoft.Win32;

namespace IPop.Notifier.Core;

/// How the Notifier opens an iPop page without piling up browser tabs.
///   - Installed iPop app: launch the app with web+ipop://open?to=/chat/… — the manifest's protocol_handlers route
///     it to /open.html inside the app, and launch_handler "focus-existing" reuses the open app window.
///   - Browser only: the open iPop tab is brought to the front first (BrowserTabs), then
///     https://host/open.html?to=/chat/… hands the page to it and closes itself.
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

    /// The installed iPop app's launch command, or null when iPop is not installed as an app.
    /// Chrome/Edge do not fill HKCU\Software\Classes\web+ipop\shell\open\command: they register the protocol as
    /// HKCU\Software\{Google\Chrome|Microsoft\Edge}\AppProtocolHandlers\&lt;ProgId&gt;\Capabilities\URLAssociations
    /// and put the command ("…\iPop.exe" --app-id=… --single-argument %1) on HKCU\Software\Classes\&lt;ProgId&gt;. Starting
    /// that command directly also skips Chrome's "Allow iPop to open this link?" prompt.
    /// Test hook: IPOP_NOTIFIER_APP_PROTOCOL=0 forces "not installed".
    public static string? AppCommand()
    {
        if (Environment.GetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL") is "0") return null;
        try
        {
            foreach (var vendor in new[] { @"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser" })
            {
                using var handlers = Registry.CurrentUser.OpenSubKey($@"Software\{vendor}\AppProtocolHandlers");
                if (handlers is null) continue;
                foreach (var progId in handlers.GetSubKeyNames())
                {
                    using var assoc = handlers.OpenSubKey($@"{progId}\Capabilities\URLAssociations");
                    if (assoc?.GetValue(Protocol) is not string id || id.Length == 0) continue;
                    using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{id}\shell\open\command");
                    if (cmd?.GetValue(null) is string c && c.Contains("%1")) return c;
                }
            }
            // Other browsers that follow the classic layout.
            using var classic = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Protocol}\shell\open\command");
            if (classic?.GetValue(null) is string cc && cc.Contains("%1")) return cc;
        }
        catch { }
        return null;
    }

    /// Splits a registry command ("C:\x\iPop.exe" --a --b %1) into the program and its arguments with %1 filled in.
    public static (string File, string Args)? ParseCommand(string command, string url)
    {
        command = command.Trim();
        string file, rest;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            file = command[1..end];
            rest = command[(end + 1)..].Trim();
        }
        else
        {
            var sp = command.IndexOf(' ');
            file = sp < 0 ? command : command[..sp];
            rest = sp < 0 ? "" : command[(sp + 1)..].Trim();
        }
        if (file.Length == 0) return null;
        // %1 sits after --single-argument; the URL is one argument with no quotes of its own (it is escaped).
        var args = rest.Contains("\"%1\"") ? rest.Replace("\"%1\"", "\"" + url + "\"") : rest.Replace("%1", url);
        return (file, args);
    }

    /// A tab/window title of the iPop app. Pages end with "— IPop" or "- IPop"/"- iPop" (Files, Notes, Projects), with
    /// an optional "(3) " unread prefix; Chrome puts the active tab in the window title as "… - IPop - Google Chrome"
    /// (it writes the em dash as a hyphen there). A hidden tab that got a page blinks "● New in iPop". Pages that are
    /// not the signed-in app (Sign in, the ONLYOFFICE "- iPop Docs" editor, install/connect pages) don't count.
    public static bool IsIPopTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (IsMarkedTitle(title)) return true;
        var t = Unread.Replace(TitlePart.Replace(title.Trim(), ""), "");
        if (!AppSuffix.IsMatch(t)) return false;
        return !NotAppPages.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public const string Mark = "● New in iPop";

    /// The iPop tab that just took a page from /open.html (session-lock.js puts this first in its title).
    public static bool IsMarkedTitle(string? title) =>
        title is not null && Unread.Replace(title.TrimStart(), "").StartsWith(Mark, StringComparison.Ordinal);

    private static readonly System.Text.RegularExpressions.Regex Unread = new(@"^\(\d+\+?\)\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TitlePart =
        new(@"\s+-\s+(Google Chrome|Microsoft​? Edge|Brave)$|\s+-\s+[^-—]+\s+-\s+Microsoft​? Edge$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex AppSuffix =
        new(@"\s[—–-]\s*i?Pop$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly string[] NotAppPages = ["Sign in", "Set password", "Install iPop", "Connect iPop Notifier", "Continue", "Opening iDesk"];
}
