using System.Net.Http.Json;

namespace IPop.Web.Services;

/// Video/audio preview by streaming (grilled 2026-11-02): the API checks access and returns a short-lived
/// ticket URL the &lt;video&gt; element reads directly with Range requests, so playback starts at once and
/// seeking does not wait for the whole file. Download stays a separate button everywhere.
public sealed class MediaStream(AuthState auth, HttpClient http)
{
    private readonly Dictionary<string, string> _urls = [];

    private sealed record Ticket(string StreamUrl, string Mime);

    public Task<string?> ChatAsync(Guid id, bool shared) => GetAsync(shared ? "shared" : "chat", id, null, null);
    public Task<string?> MailAsync(Guid id) => GetAsync("mail", id, null, null);
    public Task<string?> FileAsync(string server, string path) => GetAsync("files", null, server, path);

    private async Task<string?> GetAsync(string source, Guid? id, string? server, string? path)
    {
        var key = $"{source}|{id}|{server}|{path}";
        if (_urls.TryGetValue(key, out var hit)) return hit;
        try
        {
            var res = await auth.SendAsync(HttpMethod.Post, "/api/media/ticket", new { source, id, server, path });
            if (!res.IsSuccessStatusCode) return null;
            var t = await res.Content.ReadFromJsonAsync<Ticket>();
            if (t?.StreamUrl is not { Length: > 0 }) return null;
            var url = new Uri(http.BaseAddress!, t.StreamUrl).ToString();
            _urls[key] = url;   // tickets live 6 h; a page reload asks again
            return url;
        }
        catch { return null; }
    }

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
