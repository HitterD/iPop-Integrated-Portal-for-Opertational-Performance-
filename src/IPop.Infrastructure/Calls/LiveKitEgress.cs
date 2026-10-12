using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;

namespace IPop.Infrastructure.Calls;

/// LiveKit Egress (Twirp livekit.Egress on the LiveKit server): room-composite MP4 recordings.
public interface ILiveKitEgress
{
    bool IsConfigured { get; }
    Task<string> StartRoomCompositeAsync(string room, string filepath, string templateUrl, CancellationToken ct);
    Task StopAsync(string egressId, CancellationToken ct);
    Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct);
    /// Health check for the admin page: true when the Egress API answers at all (any 2xx, or 404).
    Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(IsConfigured);
}

public record LiveKitEgressInfo(string Status, string? Filename, long? Size, long? DurationNs, string? Error);

public sealed class LiveKitEgress(HttpClient http, ILiveKitTokenService tokens) : ILiveKitEgress
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool IsConfigured => tokens.IsConfigured;
    private Uri Base => new(tokens.ServerApiUrl!.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/') + "/twirp/livekit.Egress/");

    private async Task<JsonElement> CallAsync(string method, string? room, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, method)) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new("Bearer", tokens.GenerateAdminToken(room, record: true));
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<string> StartRoomCompositeAsync(string room, string filepath, string templateUrl, CancellationToken ct)
    {
        var r = await CallAsync("StartRoomCompositeEgress", room, new
        {
            room_name = room, layout = "speaker", custom_base_url = templateUrl,
            // EncodingOptions.audio_codec supports AAC (LiveKit protocol AudioCodec = 2).
            // preset and advanced are a protobuf oneof: send only advanced.
            advanced = new { width = 1920, height = 1080, framerate = 30, video_bitrate = 4500, audio_codec = "AAC" },
            file_outputs = new[] { new { file_type = "MP4", filepath } },
        }, ct);
        return r.GetProperty("egress_id").GetString()!;
    }

    /// ListEgress for active egresses only (no egress_id: an unknown id answers 404 and would read as "offline").
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try { await CallAsync("ListEgress", null, new { active = true }, ct); return true; }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return true; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task StopAsync(string egressId, CancellationToken ct) => await CallAsync("StopEgress", null, new { egress_id = egressId }, ct);

    public async Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct)
    {
        var r = await CallAsync("ListEgress", null, new { egress_id = egressId }, ct);
        if (!r.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
        var e = items[0];
        string? file = null; long? size = null; long? dur = null;
        if (e.TryGetProperty("file_results", out var fr) && fr.GetArrayLength() > 0)
        {
            var f = fr[0];
            file = f.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            size = f.TryGetProperty("size", out var s) ? long.Parse(s.ToString()) : null;
            dur = f.TryGetProperty("duration", out var d) ? long.Parse(d.ToString()) : null;
        }
        var status = e.TryGetProperty("status", out var st) ? st.ToString() : "";
        status = status switch { "0" => "EGRESS_STARTING", "1" => "EGRESS_ACTIVE", "2" => "EGRESS_ENDING", "3" => "EGRESS_COMPLETE", "4" => "EGRESS_FAILED", "5" => "EGRESS_ABORTED", "6" => "EGRESS_LIMIT_REACHED", _ => status };
        return new(status, file, size, dur, e.TryGetProperty("error", out var err) ? err.GetString() : null);
    }
}
