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
    private bool minimized;
    public bool Minimized
    {
        get => minimized;
        set { minimized = value; if (!value && ChatOpen) Unread = 0; }
    }
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
    /// <paramref name="password"/>: the booking's 6 digits for someone who is not invited (v2 §1.3); invited people send none.
    /// Booking errors: "no_booking", "password_required", "wrong_password", "too_many_attempts".
    public async Task<(string? Error, IReadOnlyList<string> Suggestions)> JoinAsync(string key, string? password = null)
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
                var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{key}/join",
                    string.IsNullOrEmpty(password) ? null : new { password });
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
            _ = LoadChatAsync();
            _ = RefreshCamerasAsync();
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
        chat.Clear(); chatIds.Clear(); Unread = 0; ChatOpen = false;
        Booking = null; leavingWarnedFor = null; CameraCount = 0; RoomPassword = null; passwordFor = null;
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
        // Camera permission reveals the other cameras (labels/ids), so count again once it is on.
        if (CamOn) await RefreshCamerasAsync();
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
            if (!leaving && Booking is { Leaving: true })
                toasts?.Show($"The next booking starts now. You were moved out of {room.Name}.", ToastKind.Info);
            else if (!leaving && was is "connected" or "reconnecting")
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
        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { }
        using (parsed)
        {
            var doc = parsed?.RootElement ?? default;
            try
            {
                RecordingServer = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("recording", out var r) && r.ValueKind == JsonValueKind.Object
                    ? new ServerRecording(r.GetProperty("id").GetGuid(), r.GetProperty("by").GetString() ?? "", r.GetProperty("startedAt").GetDateTime())
                    : null;
            }
            catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException) { RecordingServer = null; }
            // Booking writes can race and briefly revert: the UI just follows (no toasts), except the 1-minute
            // "next booking" notice, which fires once per takeover moment.
            Booking = ParseBooking(doc);
        }
        if (Booking is { MeetingId: var bookingMeeting } && bookingMeeting != Guid.Empty && bookingMeeting != passwordFor)
        {
            passwordFor = bookingMeeting;
            RoomPassword = null;
            _ = LoadRoomPasswordAsync(bookingMeeting);
        }
        else if (Booking is null) { passwordFor = null; RoomPassword = null; }
        if (Booking is { Leaving: true, TakeoverAt: { } takeover, NextStartAt: var next } && leavingWarnedFor != takeover)
        {
            leavingWarnedFor = takeover;
            LeavingNotices++;
            Notice?.Invoke("next_booking", (next ?? takeover.AddMinutes(10)).ToLocalTime().ToString("HH:mm"));
        }
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

    // ---------------------------------------------------------------- in-meeting chat (v2 §3)

    /// One chat line. Kind "system" = joined/left/recording lines (UserId empty). Text and Name are plain text: the
    /// overlay renders them as Razor text, never as markup.
    public record ChatLine(Guid Id, Guid UserId, string Name, string Text, DateTime SentAt, string Kind);

    public const int ChatMaxLength = 2000;
    private static readonly JsonSerializerOptions ChatJson = new(JsonSerializerDefaults.Web);
    private readonly List<ChatLine> chat = [];
    private readonly HashSet<Guid> chatIds = [];

    /// Oldest → newest, one entry per message id (history, own POST answer and the room relay all merge here).
    public IReadOnlyList<ChatLine> Chat => chat;
    /// Messages from others that arrived while the chat panel was closed (or the room minimized).
    public int Unread { get; private set; }
    public bool ChatOpen { get; private set; }

    /// The user's own id: the LiveKit identity is the iPop user id.
    public Guid MyId => Room is { } r && Guid.TryParse(r.Identity, out var id) ? id : Guid.Empty;

    public void SetChatOpen(bool open)
    {
        ChatOpen = open;
        if (open && !Minimized) Unread = 0;
        Raise();
    }

    /// Loads the booking's chat history (on join). History is not "unread": it was there before you came in.
    public async Task LoadChatAsync()
    {
        if (Room is not { } room) return;
        try
        {
            var res = await auth.SendAsync(HttpMethod.Get, $"/api/meeting-rooms/{room.Key}/chat");
            if (!res.IsSuccessStatusCode) return;
            var lines = await res.Content.ReadFromJsonAsync<List<ChatLine>>(ChatJson);
            if (Room?.Key != room.Key || lines is null) return;
            foreach (var line in lines) Merge(line, countUnread: false);
            Raise();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException) { }
    }

    /// null = sent; else "empty" | "too_long" | "rate_limited" | "not_in_room" | "no_booking" | "forbidden" | "failed".
    public async Task<string?> SendChatAsync(string text)
    {
        if (Room is not { } room) return "not_in_room";
        text = text.Trim();
        if (text.Length == 0) return "empty";
        if (text.Length > ChatMaxLength) return "too_long";
        try
        {
            var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{room.Key}/chat", new { text });
            if (res.IsSuccessStatusCode)
            {
                var line = await res.Content.ReadFromJsonAsync<ChatLine>(ChatJson);
                if (line is not null && Room?.Key == room.Key && Valid(line)) { Merge(line, countUnread: false); Raise(); }
                return null;
            }
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return "rate_limited";
            return await ReadCodeAsync(res) ?? "failed";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException) { return "failed"; }
    }

    /// A chat line relayed by the API through LiveKit (topic ipop.chat; js/meeting-rooms.js validated its shape).
    [JSInvokable]
    public void OnRoomChat(string json)
    {
        ChatLine? line;
        try { line = JsonSerializer.Deserialize<ChatLine>(json, ChatJson); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return; }
        if (line is null || Room is null || !Valid(line)) return;
        if (Merge(line, countUnread: true)) Raise();
    }

    private static bool Valid(ChatLine l) =>
        l.Id != Guid.Empty && l.Text is { Length: > 0 and <= ChatMaxLength } && l.Name is not null && l.Kind is "text" or "system";

    /// False when the id is already there (a relay of a line we already have).
    private bool Merge(ChatLine line, bool countUnread)
    {
        if (!chatIds.Add(line.Id)) return false;
        var sentAt = line.SentAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(line.SentAt, DateTimeKind.Utc) : line.SentAt.ToUniversalTime();
        line = line with { SentAt = sentAt };
        var at = chat.Count;
        while (at > 0 && chat[at - 1].SentAt > sentAt) at--;
        chat.Insert(at, line);
        if (countUnread && line.Kind == "text" && line.UserId != MyId && (!ChatOpen || Minimized)) Unread++;
        return true;
    }

    // ---------------------------------------------------------------- booking (v2 §1.5)

    /// The current booking from the room metadata. Leaving = this user is not allowed in the next booking and will be
    /// moved out at TakeoverAt (the API fills the list only from one minute before).
    public record BookingInfo(Guid OrganizerId, DateTime EndAt, DateTime? NextStartAt, DateTime? TakeoverAt, bool Leaving, Guid MeetingId = default);

    public BookingInfo? Booking { get; private set; }
    public bool IsOrganizer => Booking is { } b && MyId != Guid.Empty && b.OrganizerId == MyId;
    /// How many times the 1-minute "next booking" notice fired (once per takeover moment, whatever the metadata flicker).
    public int LeavingNotices { get; private set; }
    private DateTime? leavingWarnedFor;
    /// Injectable for tests; the overlay's countdown uses it too.
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// Extend 15 min applies to the booking only when the next booking's window starts at or after the new end.
    public bool CanExtend
    {
        get
        {
            if (!IsOrganizer || Booking is not { } b) return false;
            if (b.TakeoverAt is not { } takeover) return true;
            var start = b.EndAt > Clock() ? b.EndAt : Clock();
            return start.AddMinutes(15) <= takeover;
        }
    }

    /// (new end, null) or (null, code): "room_booked" | "forbidden" | "no_booking" | "media_unavailable" | "failed".
    public async Task<(DateTime? EndAt, string? Error)> ExtendAsync()
    {
        if (Room is not { } room) return (null, "no_booking");
        try
        {
            var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{room.Key}/extend");
            if (res.IsSuccessStatusCode)
            {
                DateTime? end = null;
                try
                {
                    var body = await res.Content.ReadFromJsonAsync<JsonElement>();
                    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("endAt", out var e) && e.TryGetDateTime(out var t))
                        end = ToUtc(t);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
                // The metadata follows within a moment; show the new end now so the banner goes away.
                if (end is { } newEnd && Booking is { } b && Room?.Key == room.Key) { Booking = b with { EndAt = newEnd }; Raise(); }
                return (end, null);
            }
            var code = await ReadCodeAsync(res);
            return (null, res.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "forbidden",
                System.Net.HttpStatusCode.ServiceUnavailable => "media_unavailable",
                System.Net.HttpStatusCode.Conflict when code is "no_booking" or "cancelled" => "no_booking",
                System.Net.HttpStatusCode.Conflict => "room_booked",
                _ => code ?? "failed",
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, "media_unavailable"); }
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage res)
    {
        try
        {
            var err = await res.Content.ReadFromJsonAsync<JsonElement>();
            return err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return null; }
    }

    private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    private BookingInfo? ParseBooking(JsonElement doc)
    {
        try
        {
            if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("booking", out var b) || b.ValueKind != JsonValueKind.Object) return null;
            static DateTime? Time(JsonElement o, string name) =>
                o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var t) ? ToUtc(t) : null;
            if (!b.TryGetProperty("organizerId", out var org) || !org.TryGetGuid(out var organizer)) return null;
            if (Time(b, "endAt") is not { } end) return null;
            var me = Room?.Identity;
            var leaving = me is not null && b.TryGetProperty("leaving", out var l) && l.ValueKind == JsonValueKind.Array
                && l.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == me);
            var meetingId = b.TryGetProperty("meetingId", out var mid) && mid.ValueKind == JsonValueKind.String && mid.TryGetGuid(out var g) ? g : Guid.Empty;
            return new BookingInfo(organizer, end, Time(b, "nextStartAt"), Time(b, "takeoverAt"), leaving, meetingId);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    // ---------------------------------------------------------------- room password (v2 §1.4)

    /// The current booking's 6 digits, for the organizer and invitees only: GET /api/meetings/{id} answers RoomPassword just
    /// to them (a password guest or anyone else gets null or 404). Never taken from room metadata.
    public string? RoomPassword { get; private set; }
    private Guid? passwordFor;

    private sealed record MeetingPasswordDto(Guid Id, string? RoomPassword);

    private async Task LoadRoomPasswordAsync(Guid meetingId)
    {
        try
        {
            var res = await auth.SendAsync(HttpMethod.Get, $"/api/meetings/{meetingId}");
            if (!res.IsSuccessStatusCode) return;
            var dto = await res.Content.ReadFromJsonAsync<MeetingPasswordDto>(ChatJson);
            if (passwordFor != meetingId || Room is null) return;
            RoomPassword = dto?.RoomPassword is { Length: 6 } pw ? pw : null;
            Raise();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException) { }
    }

    // ---------------------------------------------------------------- cameras (v2 §5)

    public int CameraCount { get; private set; }
    /// Flip is offered only when the device has more than one camera.
    public bool CanFlip => CameraCount > 1 && VideoAllowed;

    /// Browsers list every camera only after permission, so this runs on join, after the camera turns on and on devicechange.
    public async Task RefreshCamerasAsync()
    {
        if (Room is null) return;
        try { CameraCount = await js.InvokeAsync<int>("ipopRooms.listCameras"); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { CameraCount = 0; }
        Raise();
    }

    [JSInvokable]
    public Task OnDevicesChanged() => RefreshCamerasAsync();

    public record FlipResult(bool Success);

    /// True when the camera switched (front ↔ back).
    public async Task<bool> FlipAsync()
    {
        if (Room is null || !CamOn) return false;
        try { return (await js.InvokeAsync<FlipResult?>("ipopRooms.flipCamera"))?.Success ?? false; }
        catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { return false; }
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
