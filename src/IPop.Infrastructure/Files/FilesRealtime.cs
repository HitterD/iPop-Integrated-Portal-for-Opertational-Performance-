using IPop.Application.Realtime;

namespace IPop.Infrastructure.Files;

/// FilesChanged(scope) pushes. A scope is "{server}:{folder}", the folder whose listing changed
/// (server lower-cased, folder without a trailing slash, root = "/"). There is no app-level
/// membership for NAS folders (access is each user's own Synology credential), so the event goes
/// to everyone and the Files page filters by the folder it is showing; the refetch then runs
/// through the viewer's own credential, so the scope string is all that leaks.
public static class FilesRealtime
{
    /// "{server}:*" — something on that server changed but the folder is unknown (e.g. a share
    /// link deleted by id); every open view of that server reloads.
    public const string Wildcard = "*";

    public static string Scope(string server, string? dir) => $"{server.Trim().ToLowerInvariant()}:{NormalizeDir(dir)}";

    public static string NormalizeDir(string? dir)
    {
        var d = (dir ?? "").Trim().Replace('\\', '/');
        if (d.Length == 0) return "/";
        if (d == Wildcard) return Wildcard;
        if (!d.StartsWith('/')) d = "/" + d;
        d = d.TrimEnd('/');
        return d.Length == 0 ? "/" : d;
    }

    /// The folder that lists `path`.
    public static string ParentDir(string path)
    {
        var p = NormalizeDir(path);
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    /// Best effort: the NAS change already happened, so a push failure is logged by nobody and
    /// never surfaces to the caller. Not tied to a request token on purpose.
    public static async Task PushAsync(IRealtime realtime, string server, IEnumerable<string?> dirs)
    {
        foreach (var scope in dirs.Select(d => Scope(server, d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { await realtime.ToAllAsync(RealtimeEvents.FilesChanged, scope); }
            catch (Exception) { /* advisory; clients also reload on navigation and reconnect */ }
        }
    }
}
