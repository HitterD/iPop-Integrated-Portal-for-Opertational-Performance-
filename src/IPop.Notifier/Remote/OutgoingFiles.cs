using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IPop.Notifier.Remote;

/// Files the person picked to send during remote help (BIGIS #8 v3: "Send a file to ICT", clipboard files). The page
/// never gets the bytes from the app: it fetches http://files.notifier.ipop/{token} and streams the response straight
/// into LiveKit, so a 2 GB file never sits in memory. Only files registered here are served, each under a random
/// token; tokens are dropped when the session ends.
public static class OutgoingFiles
{
    public const string Host = "files.notifier.ipop";
    private static readonly ConcurrentDictionary<string, string> Files = new();

    /// Registers a picked file; returns its token (or null when it can't be read).
    public static string? Add(string path)
    {
        try { if (!File.Exists(path)) return null; } catch { return null; }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Files[token] = path;
        return token;
    }

    public static string Url(string token) => $"http://{Host}/{token}";

    /// The file for a request path "/{token}"; null when unknown.
    public static string? Resolve(string? urlPath)
    {
        var token = (urlPath ?? "").Trim('/');
        return token.Length == 32 && Files.TryGetValue(token, out var p) ? p : null;
    }

    public static void Forget(string token) => Files.TryRemove(token, out _);
    public static void Clear() => Files.Clear();
}
