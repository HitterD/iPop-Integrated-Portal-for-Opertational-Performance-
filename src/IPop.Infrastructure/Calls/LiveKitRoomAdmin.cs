using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;

namespace IPop.Infrastructure.Calls;

/// Server-side LiveKit RoomService (Twirp over HTTP): who is in a room, create it with a cap, change what a
/// participant may publish, mute or remove someone. The http(s) base comes from the same Calls:LiveKit:Url as the clients.
public interface ILiveKitRoomAdmin
{
    bool IsConfigured { get; }
    /// Participants in a room (empty when the room does not exist).
    Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct);
    /// Mutes every published microphone track of the participant. False when they are not in the room.
    Task<bool> MuteAsync(string room, string identity, CancellationToken ct);
    Task<bool> RemoveAsync(string room, string identity, CancellationToken ct);
    /// Creates the room (or updates an existing one) with a hard participant cap.
    Task CreateRoomAsync(string room, int maxParticipants, CancellationToken ct);
    Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct);
    /// Replaces what the participant may publish; tracks of sources no longer allowed are muted at once.
    Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct);
}

public record LiveKitParticipant(string Identity, string Name, IReadOnlyList<LiveKitTrack> Tracks, IReadOnlyDictionary<string, string> Attributes);

public record LiveKitTrack(string Sid, string Source, bool Muted)
{
    private static readonly Dictionary<string, string> Numbers = new() { ["CAMERA"] = "1", ["MICROPHONE"] = "2", ["SCREEN_SHARE"] = "3", ["SCREEN_SHARE_AUDIO"] = "4" };
    /// The server may send the enum as its name or its number.
    public bool IsSource(string upperName) => Source == upperName || (Numbers.TryGetValue(upperName, out var n) && Source == n);
}

public sealed class LiveKitRoomAdmin(HttpClient http, ILiveKitTokenService tokens) : ILiveKitRoomAdmin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => tokens.IsConfigured;

    private Uri Base => new(tokens.ServerUrl!.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/') + "/twirp/livekit.RoomService/");

    private async Task<JsonElement?> CallAsync(string method, string room, object body, CancellationToken ct, bool allowNotFound = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, method)) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new("Bearer", tokens.GenerateAdminToken(room));
        using var res = await http.SendAsync(req, ct);
        // Twirp "not_found" (unknown room/participant) comes back as 404.
        if (allowNotFound && res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct)
    {
        var r = await CallAsync("ListParticipants", room, new { room }, ct);
        if (r is not { } doc || !doc.TryGetProperty("participants", out var list)) return [];
        return list.EnumerateArray().Select(p => new LiveKitParticipant(
            p.GetProperty("identity").GetString() ?? "",
            p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            p.TryGetProperty("tracks", out var ts)
                ? ts.EnumerateArray().Select(t => new LiveKitTrack(
                    t.GetProperty("sid").GetString() ?? "",
                    t.TryGetProperty("source", out var s) ? s.ToString() : "",
                    t.TryGetProperty("muted", out var m) && m.GetBoolean())).ToList()
                : [],
            p.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "")
                : new Dictionary<string, string>())).ToList();
    }

    public async Task<bool> MuteAsync(string room, string identity, CancellationToken ct)
    {
        var p = (await ListAsync(room, ct)).FirstOrDefault(x => x.Identity == identity);
        if (p is null) return false;
        foreach (var t in p.Tracks.Where(t => !t.Muted && t.IsSource("MICROPHONE")))
            if (await CallAsync("MutePublishedTrack", room, new { room, identity, track_sid = t.Sid, muted = true }, ct) is null)
                return false;
        return true;
    }

    public async Task<bool> RemoveAsync(string room, string identity, CancellationToken ct) =>
        await CallAsync("RemoveParticipant", room, new { room, identity }, ct) is not null;

    public async Task CreateRoomAsync(string room, int maxParticipants, CancellationToken ct) =>
        await CallAsync("CreateRoom", room, new { name = room, max_participants = maxParticipants, empty_timeout = 300 }, ct, allowNotFound: false);

    public async Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct) =>
        await CallAsync("UpdateRoomMetadata", room, new { room, metadata }, ct, allowNotFound: false);

    public async Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var p = (await ListAsync(room, ct)).FirstOrDefault(x => x.Identity == identity);
        if (p is null) return false;
        var upper = sources.Select(s => s.ToUpperInvariant()).ToList();
        if (await CallAsync("UpdateParticipant", room, new
        {
            room, identity,
            permission = new { can_subscribe = true, can_publish = upper.Count > 0, can_publish_data = true, can_update_metadata = true, can_publish_sources = upper },
        }, ct) is null)
            return false;
        foreach (var t in p.Tracks.Where(t => !t.Muted && !upper.Any(t.IsSource)))
            if (await CallAsync("MutePublishedTrack", room, new { room, identity, track_sid = t.Sid, muted = true }, ct) is null)
                return false;
        return true;
    }
}
