using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.MeetingRooms;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// The meeting room the user is in (one at a time). Lives for the session so the meeting keeps running while
/// the user moves between modules; MeetingRoomOverlay draws it, js/meeting-rooms.js owns the media.
public sealed class MeetingRoomState : IAsyncDisposable
{
    private readonly AuthState auth;
    private readonly IJSRuntime js;
    private readonly CallState calls;
    private readonly ToastService? toasts;
    private bool leaving;

    public MeetingRoomState(AuthState auth, IJSRuntime js, CallState calls, ToastService? toasts = null)
    {
        this.auth = auth; this.js = js; this.calls = calls; this.toasts = toasts;
        // A 1:1/group call or scheduled meeting is about to use the microphone: one at a time, so leave the room.
        calls.MediaStarting += OnCallMediaStartingAsync;
    }

    private async Task OnCallMediaStartingAsync()
    {
        if (Room is not { } room) return;
        if (RecordingPc)
        {
            try { await StopPcRecordingAsync(); }
            catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { }
        }
        await LeaveAsync();
        toasts?.Show($"You left {room.Name} to take the call.", ToastKind.Info);
    }

    public record Person(string Identity, string Name, bool Speaking, bool MicOn, bool CamOn, bool Sharing, bool RecPc, bool IsLocal);
    public record ServerRecording(Guid Id, string By, DateTime StartedAt);

    private DotNetObjectReference<MeetingRoomState>? selfRef;
    private bool joining;

    public MeetingRoomJoinDto? Room { get; private set; }
    public bool Minimized { get; set; }
    public bool MicOn { get; private set; }
    public bool CamOn { get; private set; }
    public bool Sharing { get; private set; }
    public bool RecordingPc { get; private set; }
    public ServerRecording? RecordingServer { get; private set; }
    public bool VideoAllowed { get; private set; }
    public IReadOnlyList<Person> People { get; private set; } = [];
    public string ConnectionState { get; private set; } = "";
    public DateTime JoinedAt { get; private set; }
    /// Days a server recording is kept (from the join answer), for the Record menu.
    public int RetentionDays { get; private set; } = 90;
    /// The booking running in the room when we joined (null = ad-hoc). Shown in the top bar.
    public string? MeetingTitle { get; private set; }

    public event Action? Changed;
    public event Action<string, string?>? Notice;

    private void Raise() => Changed?.Invoke();

    /// null = joined; else an error code ("room_full" with suggestions, "in_call", "media_unavailable", "connect_failed", …).
    /// Never throws: HTTP/JSON failures return "media_unavailable"; a media failure after the room was set returns
    /// "connect_failed" and leaves no half-joined room behind, so a retry of the same key really connects.
    public async Task<(string? Error, IReadOnlyList<string> Suggestions)> JoinAsync(string key)
    {
        if (Room?.Key == key) { Minimized = false; Raise(); return (null, []); }
        if (calls.HasLiveCall) return ("in_call", []);
        if (joining) return ("busy", []);
        joining = true;
        try
        {
            if (Room is not null) await LeaveAsync();
            MeetingRoomJoinDto? dto;
            try
            {
                var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{key}/join");
                if (!res.IsSuccessStatusCode) return await ReadJoinErrorAsync(res);
                dto = await res.Content.ReadFromJsonAsync<MeetingRoomJoinDto>();
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
            {
                return ("media_unavailable", []);
            }
            if (dto is null) return ("media_unavailable", []);

            selfRef ??= DotNetObjectReference.Create(this);
            Room = dto; VideoAllowed = dto.VideoAllowed; Minimized = false; JoinedAt = DateTime.UtcNow;
            RetentionDays = dto.RetentionDays > 0 ? dto.RetentionDays : 90;
            MicOn = CamOn = Sharing = RecordingPc = false; RecordingServer = null; People = []; ConnectionState = "connecting";
            MeetingTitle = null;
            Raise();
            bool ok;
            try
            {
                ok = await js.InvokeAsync<bool>("ipopRooms.join", dto.Url, dto.Token, selfRef,
                    new { stageId = "mr-stage", stripId = "mr-strip", inputDeviceId = calls.InputDeviceId });
            }
            catch (Exception e) when (e is JSException or TaskCanceledException or InvalidOperationException)
            {
                ok = false;
            }
            if (!ok)
            {
                leaving = true;
                try { await js.InvokeVoidAsync("ipopRooms.leave"); }
                catch (Exception e) when (e is JSException or TaskCanceledException or InvalidOperationException) { }
                finally { leaving = false; }
                Clear();
                Raise();
                return ("connect_failed", []);
            }
            _ = LoadTitleAsync(dto.Key);
            return (null, []);
        }
        finally { joining = false; }
    }

    private static async Task<(string? Error, IReadOnlyList<string> Suggestions)> ReadJoinErrorAsync(HttpResponseMessage res)
    {
        var code = "media_unavailable";
        List<string> sug = [];
        try
        {
            var err = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { } s) code = s;
                if (err.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    sug = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { }
        return (code, sug);
    }

    private async Task LoadTitleAsync(string key)
    {
        try
        {
            var res = await auth.SendAsync(HttpMethod.Get, "/api/meeting-rooms");
            if (!res.IsSuccessStatusCode) return;
            var rooms = await res.Content.ReadFromJsonAsync<List<MeetingRoomDto>>();
            if (Room?.Key != key) return;
            MeetingTitle = rooms?.FirstOrDefault(r => r.Key == key)?.Now?.Title;
            Raise();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { }
    }

    public async Task LeaveAsync()
    {
        if (Room is null) return;
        leaving = true;
        try { await js.InvokeVoidAsync("ipopRooms.leave"); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { }
        finally
        {
            leaving = false;
            // Whatever the browser did, the old room must not linger (JoinAsync's room switch relies on it).
            Clear();
            Raise();
        }
    }

    private void Clear()
    {
        Room = null; People = []; MicOn = CamOn = Sharing = RecordingPc = false; RecordingServer = null;
        Minimized = false; MeetingTitle = null; ConnectionState = "";
    }

    public async Task ToggleMicAsync()
    {
        if (Room is null) return;
        MicOn = await js.InvokeAsync<bool>("ipopRooms.setMic", !MicOn); Raise();
    }

    public async Task ToggleCameraAsync()
    {
        if (Room is null || !VideoAllowed) return;
        CamOn = await js.InvokeAsync<bool>("ipopRooms.setCamera", !CamOn); Raise();
    }

    /// "ok" | "busy" (someone shares — ask, then call with takeover: true) | "denied"
    public async Task<string> ToggleShareAsync(bool takeover = false)
    {
        if (Room is null) return "denied";
        // Capture the intent: OnRoomPeople may already flip Sharing while the browser picker is open.
        var target = !Sharing;
        var r = await js.InvokeAsync<string>("ipopRooms.setShare", target, takeover);
        if (r == "ok") Sharing = target;
        Raise();
        return r;
    }

    public async Task StartPcRecordingAsync()
    {
        if (Room is null) return;
        RecordingPc = await js.InvokeAsync<bool>("ipopRooms.startPcRecording"); Raise();
    }

    public async Task StopPcRecordingAsync() { await js.InvokeVoidAsync("ipopRooms.stopPcRecording"); RecordingPc = false; Raise(); }

    /// null = started; else error code from the API ("already_recording", "egress_unavailable", …)
    public async Task<string?> StartServerRecordingAsync()
    {
        if (Room is null) return "not_in_room";
        return await PostForErrorAsync($"/api/meeting-rooms/{Room.Key}/recordings");
    }

    /// null = stop requested (the banner clears when the room metadata changes); else an error code
    /// ("not_found", "not_in_room", "egress_unavailable", …).
    public async Task<string?> StopServerRecordingAsync()
    {
        if (RecordingServer is not { } rec) return "not_found";
        return await PostForErrorAsync($"/api/meeting-rooms/recordings/{rec.Id}/stop");
    }

    private async Task<string?> PostForErrorAsync(string url)
    {
        try
        {
            var res = await auth.SendAsync(HttpMethod.Post, url);
            if (res.IsSuccessStatusCode) return null;
            try
            {
                var err = await res.Content.ReadFromJsonAsync<JsonElement>();
                return err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : "egress_unavailable";
            }
            catch (Exception e) when (e is JsonException or NotSupportedException) { return "egress_unavailable"; }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "egress_unavailable"; }
    }

    [JSInvokable]
    public void OnRoomState(string state)
    {
        var was = ConnectionState;
        ConnectionState = state;
        if (state == "disconnected" && Room is { } room)
        {
            // Dropped by the network or the server (not our Leave, not a join that never connected): say so.
            if (!leaving && was is "connected" or "reconnecting")
                toasts?.Show($"You were disconnected from {room.Name}.", ToastKind.Warning);
            Clear();
        }
        Raise();
    }

    [JSInvokable]
    public void OnRoomPeople(Person[] people)
    {
        People = people;
        var me = people.FirstOrDefault(p => p.IsLocal);
        if (me is not null) { MicOn = me.MicOn; CamOn = me.CamOn; Sharing = me.Sharing; RecordingPc = me.RecPc; }
        Raise();
    }

    [JSInvokable]
    public void OnRoomMetadata(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var doc = parsed.RootElement;
            RecordingServer = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("recording", out var r) && r.ValueKind == JsonValueKind.Object
                ? new ServerRecording(r.GetProperty("id").GetGuid(), r.GetProperty("by").GetString() ?? "", r.GetProperty("startedAt").GetDateTime())
                : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { RecordingServer = null; }
        Raise();
    }

    [JSInvokable]
    public void OnRoomNotice(string code, string? name)
    {
        if (code == "video_off") { VideoAllowed = false; CamOn = false; }
        if (code == "video_on") VideoAllowed = true;
        if (code == "share_taken") Sharing = false;
        if (code == "muted_by_server") MicOn = false;
        Notice?.Invoke(code, name);
        Raise();
    }

    internal void SetForTest(MeetingRoomJoinDto room, string connection = "connected")
    {
        Room = room; VideoAllowed = room.VideoAllowed; JoinedAt = DateTime.UtcNow; RetentionDays = room.RetentionDays; ConnectionState = connection; Raise();
    }

    public async ValueTask DisposeAsync()
    {
        calls.MediaStarting -= OnCallMediaStartingAsync;
        try { await LeaveAsync(); } catch (JSDisconnectedException) { } catch (TaskCanceledException) { }
        selfRef?.Dispose();
    }
}
