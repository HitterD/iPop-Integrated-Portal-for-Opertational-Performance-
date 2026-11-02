using System.Net.Http.Json;

namespace IPop.Web.Services;

/// Video/audio preview by streaming (grilled 2026-11-02): the API checks access and returns a short-lived ticket URL
/// the &lt;video&gt; element reads directly with Range requests, so playback starts at once and seeking does not wait for
/// the whole file. Download stays a separate button everywhere.
///
/// NAS files are copied to the server first: the stream answers 202 while that runs, and <see cref="WhenReadyAsync"/>
/// waits for it (the player shows "Preparing video…"). Tickets last 2 h; a cached one is renewed 10 min before.
public sealed class MediaStream(AuthState auth, HttpClient http)
{
    private readonly Dictionary<string, (string Url, DateTime Expires)> _urls = [];

    private sealed record Ticket(string StreamUrl, string Mime, DateTime ExpiresAt);

    /// Ticket URL, or null with <see cref="LastError"/> set ("not_media", "not_found", "network"…).
    public Task<string?> ChatAsync(Guid id, bool shared) => GetAsync(shared ? "shared" : "chat", id, null, null, null, null);
    public Task<string?> MailAsync(Guid id) => GetAsync("mail", id, null, null, null, null);
    public Task<string?> FileAsync(string server, string path, long size = 0, long mtime = 0) => GetAsync("files", null, server, path, size, mtime);

    public string? LastError { get; private set; }

    /// Drops a cached ticket (after a player error) so the next call asks the server again.
    public void Forget(string? url)
    {
        if (url is null) return;
        foreach (var k in _urls.Where(p => p.Value.Url == url).Select(p => p.Key).ToList()) _urls.Remove(k);
    }

    private async Task<string?> GetAsync(string source, Guid? id, string? server, string? path, long? size, long? mtime)
    {
        LastError = null;
        var key = $"{source}|{id}|{server}|{path}|{size}|{mtime}";
        if (_urls.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow.AddMinutes(10)) return hit.Url;
        try
        {
            var res = await auth.SendAsync(HttpMethod.Post, "/api/media/ticket", new { source, id, server, path, size, mtime });
            if (!res.IsSuccessStatusCode)
            {
                LastError = (int)res.StatusCode switch { 415 => "not_media", 404 => "not_found", 401 or 403 => "denied", _ => "server" };
                return null;
            }
            var t = await res.Content.ReadFromJsonAsync<Ticket>();
            if (t?.StreamUrl is not { Length: > 0 }) { LastError = "server"; return null; }
            var url = new Uri(http.BaseAddress!, t.StreamUrl).ToString();
            _urls[key] = (url, t.ExpiresAt == default ? DateTime.UtcNow.AddHours(1) : t.ExpiresAt);
            return url;
        }
        catch { LastError = "network"; return null; }
    }

    /// Waits until the server has the file ready to stream (NAS copies answer 202 "preparing" first).
    /// Returns null when ready, else a short reason ("denied", "not_found", "nas", "timeout").
    public async Task<string?> WhenReadyAsync(string url, Func<bool> stillWanted, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        while (stillWanted() && !ct.IsCancellationRequested)
        {
            HttpResponseMessage res;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                res = await http.SendAsync(req, ct);
            }
            catch { return "network"; }
            using (res)
            {
                switch ((int)res.StatusCode)
                {
                    case 200 or 206: return null;
                    case 202: break;
                    case 401 or 403: return "denied";
                    case 404: return "not_found";
                    default: return "nas";
                }
            }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(30)) return "timeout";
            try { await Task.Delay(2000, ct); } catch { return "timeout"; }
        }
        return "cancelled";
    }

    /// The message for a preview that could not play, by reason.
    public static string Explain(string? reason, bool video = true) => reason switch
    {
        "denied" => "You no longer have access to this file.",
        "not_found" => "This file is no longer available.",
        "nas" => "The file could not be read from the NAS. Try again or use Download.",
        "network" => "Could not reach iPop. Check your connection and try again.",
        "timeout" => "Preparing took too long. Use Download instead.",
        _ => video ? "This video can't be previewed in the browser. Download it to watch." : "This audio can't be played in the browser. Download it to listen.",
    };

    /// Browsers play MP4 (H.264/AAC), WebM and Ogg; other containers (AVI, WMV, MKV, FLV, 3GP, HEVC .mov from
    /// iPhones) usually fail, so the player shows "can't be previewed" and Download instead of a black box.
    public static bool LikelyPlayable(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext is ".mp4" or ".m4v" or ".webm" or ".ogv" or ".ogg" or ".mov" or ".mp3" or ".m4a" or ".aac" or ".wav" or ".opus" or ".oga" or ".flac";
    }

    public static bool IsVideoName(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() is ".mp4" or ".m4v" or ".webm" or ".ogv" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".3gp" or ".mpeg" or ".mpg";
}
