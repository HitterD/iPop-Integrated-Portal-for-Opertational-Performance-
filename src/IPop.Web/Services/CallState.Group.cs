using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Group calls (Kind "group"): media over the LiveKit SFU through window.ipopGroupCall
/// (js/livekit-call.js). A decline or hang-up only affects me; the server ends the call when
/// the last participant leaves. Every change to a chat group's call is also published through
/// <see cref="GroupCallChanged"/> so the chat banner can show "Panggilan grup sedang berlangsung".
/// </summary>
public partial class CallState
{
    private Guid? _groupMediaCallId;
    private string? _groupInputDeviceId;
    private readonly SemaphoreSlim _groupGate = new(1, 1);
    private CancellationTokenSource? _groupRingCts;

    /// Live group calls by chat group (from hub broadcasts / banner fetches).
    private readonly Dictionary<Guid, CallRoomDto> _groupCalls = [];

    /// The current call runs on LiveKit with group semantics: a chat-group call or an ad-hoc
    /// call (a 1:1 someone added a person to).
    public bool IsGroup => CallKind.IsLiveKit(Current?.Kind);

    /// An ad-hoc multi-party call (no chat group behind it).
    public bool IsAdHoc => Current?.Kind == CallKind.AdHoc;

    /// Group name for the ring card and call window; ad-hoc calls list the other people.
    public string GroupName => IsAdHoc ? AdHocName(Current!)
        : string.IsNullOrWhiteSpace(Current?.Title) ? "Panggilan grup" : Current!.Title!;

    /// "Call with Jason, Beny, +1": everyone but me who is in or being called into the call.
    private string AdHocName(CallRoomDto room)
    {
        var names = room.Participants
            .Where(p => p.Nik != _auth.Me?.Nik && p.Status is CallParticipantStatus.Accepted or CallParticipantStatus.Invited)
            .Select(p => FirstNameOf(p.Name)).ToList();
        if (names.Count == 0) return "Call";
        var shown = string.Join(", ", names.Take(2));
        return names.Count > 2 ? $"Call with {shown}, +{names.Count - 2}" : $"Call with {shown}";
    }

    /// Ring text for someone added to a running call: "Beny invited you to a call with Jason"
    /// or "Beny invited you to ICT IPOP call". Null for an ordinary ring.
    public string? InviteLine
    {
        get
        {
            if (Current is not { } room || ActiveMe is not { InvitedById: { } by } me) return null;
            var inviter = FirstNameOf(room.Participants.FirstOrDefault(p => p.UserId == by)?.Name ?? "Someone");
            if (room.Kind == CallKind.Group)
                return $"{inviter} invited you to {GroupName} call";
            var others = room.Participants
                .Where(p => p.UserId != me.UserId && p.UserId != by && p.Status == CallParticipantStatus.Accepted)
                .Select(p => FirstNameOf(p.Name)).ToList();
            return others.Count == 0 ? $"{inviter} invited you to a call"
                : $"{inviter} invited you to a call with {string.Join(", ", others.Take(3))}{(others.Count > 3 ? $", +{others.Count - 3}" : "")}";
        }
    }

    /// When my ring started: my own invite time (people added later ring in full), else the call start.
    public static DateTime RingStart(CallRoomDto room, CallParticipantDto? p) =>
        DateTime.SpecifyKind(p is { Status: CallParticipantStatus.Invited, InvitedById: not null, JoinedAt: { } at } ? at : room.StartedAt, DateTimeKind.Utc);

    /// The participant is still being rung (invited, inside their ring window).
    public bool IsRinging(CallParticipantDto p) =>
        Current is { } room && p.Status == CallParticipantStatus.Invited
        && DateTime.UtcNow - RingStart(room, p) < TimeSpan.FromSeconds(RingTimeoutSeconds);

    private static string FirstNameOf(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    /// "Add person": ring a directory user into the current call. Null on success, else the error
    /// to show. Adding to a 1:1 call switches it to LiveKit (the response carries my token).
    public async Task<string?> InviteAsync(string nik)
    {
        if (Current is not { } room || IsMeeting) return "There is no call to add people to.";
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Post, $"/api/calls/{room.Id}/invite", new { nik });
            if (!res.IsSuccessStatusCode)
            {
                try
                {
                    var err = await res.Content.ReadFromJsonAsync<CallApiError>();
                    if (!string.IsNullOrWhiteSpace(err?.Message)) return err.Message;
                }
                catch (JsonException) { }
                return res.StatusCode == HttpStatusCode.ServiceUnavailable
                    ? "Adding people is not available: the call media server is not configured."
                    : $"Could not add that person ({(int)res.StatusCode}).";
            }
            var after = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (after is not null && Current?.Id == after.Id) await HandleCallRoomChangedAsync(after);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return "The call server could not be reached.";
        }
    }

    /// My P2P 1:1 call became an ad-hoc LiveKit call: drop the peer connection and join the
    /// LiveKit room with a fresh token (a short audio gap). Current switches to the ad-hoc room
    /// first, so later updates take the group path and this never runs twice for one call.
    private async Task UpgradeToLiveKitAsync(CallRoomDto room)
    {
        var wasMuted = ActiveMe?.IsMuted == true;
        Current = room;
        _webRtcActiveCallId = null;
        _peerConnection = null;
        _peerCallId = null;
        WebRtcState = "connecting";
        StateChanged?.Invoke();
        try { await _js.InvokeVoidAsync("ipopAudio.endCall"); } catch (JSException) { }

        var joined = room;
        if (string.IsNullOrEmpty(room.LiveKitToken))
        {
            try
            {
                using var res = await _auth.SendAsync(HttpMethod.Post, $"/api/calls/{room.Id}/join");
                joined = res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<CallRoomDto>() ?? room : room;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        if (Current?.Id != room.Id) return; // ended meanwhile
        if (string.IsNullOrEmpty(joined.LiveKitUrl) || string.IsNullOrEmpty(joined.LiveKitToken))
        {
            WebRtcState = "failed";
            LastError = "Could not switch the call to multi-party audio.";
            StateChanged?.Invoke();
            return;
        }
        Current = Current with { LiveKitUrl = joined.LiveKitUrl, LiveKitToken = joined.LiveKitToken, LiveKitRoom = joined.LiveKitRoom };
        _groupMediaCallId = room.Id;
        _selfRef ??= DotNetObjectReference.Create(this);
        try
        {
            var ok = await _js.InvokeAsync<bool>("ipopGroupCall.join", joined.LiveKitUrl, joined.LiveKitToken, _selfRef, _groupInputDeviceId);
            if (!ok) WebRtcState = "failed";
            else if (wasMuted) await _js.InvokeVoidAsync("ipopGroupCall.setMuted", true);
        }
        catch (JSException ex)
        {
            WebRtcState = "failed";
            LastError = "Could not switch the call to multi-party audio: " + ex.Message;
        }
        StateChanged?.Invoke();
    }

    /// I am connected (accepted) in the current group call.
    public bool IsInGroupCall =>
        IsGroup && HasLiveCall && ActiveMe?.Status == CallParticipantStatus.Accepted;

    /// LiveKit participants keyed by identity (NIK): speaking / muted / level.
    public IReadOnlyDictionary<string, GroupMediaParticipant> GroupMedia => _groupMedia;
    private Dictionary<string, GroupMediaParticipant> _groupMedia = [];

    /// Group participants currently in the call (accepted), from the server state.
    public int GroupJoinedCount =>
        Current?.Participants.Count(p => p.Status == CallParticipantStatus.Accepted) ?? 0;

    /// A chat group's call changed (started, someone joined/left, ended). Argument: chat room id.
    public event Action<Guid>? GroupCallChanged;

    /// The chat group's live call, if known (hub-updated; call <see cref="FetchGroupCallAsync"/> once on open).
    public CallRoomDto? GroupCallFor(Guid chatRoomId) =>
        _groupCalls.TryGetValue(chatRoomId, out var r) && r.Status is CallStatus.Ringing or CallStatus.Active ? r : null;

    public async Task<CallRoomDto?> FetchGroupCallAsync(Guid chatRoomId)
    {
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/group/{chatRoomId}/active");
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync();
            var room = string.IsNullOrWhiteSpace(text) || text == "null"
                ? null
                : JsonSerializer.Deserialize<CallRoomDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (room is null) _groupCalls.Remove(chatRoomId);
            else _groupCalls[chatRoomId] = room;
            GroupCallChanged?.Invoke(chatRoomId);
            return room;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// Start (or join the live) call of a chat group.
    public Task<CallRoomDto?> StartGroupAsync(Guid chatRoomId) =>
        EnterGroupAsync($"/api/calls/group/{chatRoomId}");

    /// Join a group call: accept its ring, or late join from the chat banner.
    public Task<CallRoomDto?> JoinGroupAsync(Guid callId) =>
        EnterGroupAsync($"/api/calls/{callId}/join");

    private async Task<CallRoomDto?> EnterGroupAsync(string url)
    {
        if (PracticeCall?.Invoke("") == true) return null;
        if (HasLiveCall && !(IsGroup && ActiveMe?.Status == CallParticipantStatus.Invited))
        {
            LastError = "Masih ada panggilan yang berlangsung.";
            StateChanged?.Invoke();
            return null;
        }
        if (!await _groupGate.WaitAsync(0)) return null;
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
            using var res = await _auth.SendAsync(HttpMethod.Post, url);
            if (!res.IsSuccessStatusCode)
            {
                try
                {
                    var err = await res.Content.ReadFromJsonAsync<CallApiError>();
                    LastError = err?.Message ?? $"Server returned {(int)res.StatusCode}";
                }
                catch { LastError = $"Server returned {(int)res.StatusCode}"; }
                if (IsGroup && ActiveMe?.Status == CallParticipantStatus.Invited) Current = null;
                StateChanged?.Invoke();
                return null;
            }
            var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (room is null) return null;
            LastError = null;
            Current = room;
            TrackGroupCall(room);
            CancelGroupRing();
            StateChanged?.Invoke();

            if (!string.IsNullOrEmpty(room.LiveKitUrl) && !string.IsNullOrEmpty(room.LiveKitToken))
            {
                _groupMediaCallId = room.Id;
                _selfRef ??= DotNetObjectReference.Create(this);
                WebRtcState = "connecting";
                StateChanged?.Invoke();
                var ok = await _js.InvokeAsync<bool>("ipopGroupCall.join", room.LiveKitUrl, room.LiveKitToken, _selfRef, _groupInputDeviceId);
                if (!ok) WebRtcState = "failed";
            }
            else
            {
                WebRtcState = "failed";
                LastError = "Server media panggilan grup tidak tersedia.";
            }
            StateChanged?.Invoke();
            return Current;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or JSException)
        {
            LastError = "Panggilan grup gagal: " + ex.Message;
            StateChanged?.Invoke();
            return null;
        }
        finally
        {
            _groupGate.Release();
        }
    }

    private async Task<CallRoomDto?> DeclineGroupAsync()
    {
        var id = Current!.Id;
        CancelGroupRing();
        await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
        Current = null;
        StateChanged?.Invoke();
        try { using var _ = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal", new { callId = id, action = "decline" }); }
        catch (HttpRequestException) { }
        return null;
    }

    private async Task<CallRoomDto?> LeaveGroupAsync()
    {
        if (IsMeeting) return await LeaveMeetingAsync();
        var room = Current!;
        await LeaveGroupMediaAsync();
        Current = null;
        WebRtcState = null;
        _groupMedia = [];
        var left = room.Kind == CallKind.AdHoc ? "Left the call" : "Keluar dari panggilan grup";
        LastEndReason = room.EstablishedAt is { } est
            ? $"{left} · {FormatDuration(DateTime.UtcNow - DateTime.SpecifyKind(est, DateTimeKind.Utc))}"
            : left;
        StateChanged?.Invoke();
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal", new { callId = room.Id, action = "leave" });
            if (res.IsSuccessStatusCode && await res.Content.ReadFromJsonAsync<CallRoomDto>() is { } after)
                TrackGroupCall(after);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { }
        try { await _js.InvokeVoidAsync("ipopAudio.playEndTone"); } catch (JSException) { }
        CallEnded?.Invoke();
        return null;
    }

    private async Task LeaveGroupMediaAsync()
    {
        _groupMediaCallId = null;
        try { await _js.InvokeVoidAsync("ipopGroupCall.leave"); } catch (JSException) { }
    }

    private void TrackGroupCall(CallRoomDto room)
    {
        if (room.ChatRoomId is not { } chatId) return;
        if (room.Status is CallStatus.Ringing or CallStatus.Active) _groupCalls[chatId] = room;
        else _groupCalls.Remove(chatId);
        GroupCallChanged?.Invoke(chatId);
    }

    /// Hub/REST update of a group call.
    private async Task HandleGroupRoomAsync(CallRoomDto room)
    {
        TrackGroupCall(room);
        var myNik = _auth.Me?.Nik;
        var me = room.Participants.FirstOrDefault(p => p.Nik == myNik);
        var live = room.Status is CallStatus.Ringing or CallStatus.Active;

        if (Current?.Id == room.Id)
        {
            if (!live)
            {
                // Server closed it (last one left, ring timeout, or I dropped).
                if (IsInGroupCall || _groupMediaCallId == room.Id)
                    await HandleCallEndedAsync(room.Status == CallStatus.Missed
                        ? "Tidak ada anggota yang bergabung"
                        : room.EstablishedAt is { } est && room.EndedAt is { } end
                            ? $"{(room.Kind == CallKind.AdHoc ? "Call ended" : "Panggilan grup selesai")} · {FormatDuration(end - est)}"
                            : room.Kind == CallKind.AdHoc ? "Call ended" : "Panggilan grup selesai");
                else
                {
                    CancelGroupRing();
                    await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
                    Current = null;
                    StateChanged?.Invoke();
                }
                return;
            }
            // Keep my token/url from the join response; only the room state changes.
            Current = room with { LiveKitUrl = Current.LiveKitUrl, LiveKitToken = Current.LiveKitToken, LiveKitRoom = Current.LiveKitRoom };
            if (me?.Status is CallParticipantStatus.Declined or CallParticipantStatus.Left && _groupMediaCallId != room.Id)
            {
                // Declined / left from another tab.
                CancelGroupRing();
                await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
                Current = null;
            }
            StateChanged?.Invoke();
            return;
        }

        // Someone else's view of a call I'm not showing: ring me if I'm invited and it's fresh.
        if (live && !HasLiveCall && me?.Status == CallParticipantStatus.Invited
            && DateTime.UtcNow - RingStart(room, me) < TimeSpan.FromSeconds(RingTimeoutSeconds))
        {
            Current = room;
            LastError = null;
            await _js.InvokeVoidAsync("ipopAudio.playRingtone");
            ScheduleGroupRingStop(room);
            StateChanged?.Invoke();
        }
    }

    /// The group ring stops after the ring window; the call itself may go on (join via the banner).
    private void ScheduleGroupRingStop(CallRoomDto room)
    {
        CancelGroupRing();
        _groupRingCts = new CancellationTokenSource();
        var token = _groupRingCts.Token;
        var left = TimeSpan.FromSeconds(RingTimeoutSeconds)
            - (DateTime.UtcNow - RingStart(room, room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik)));
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        _ = Task.Delay(left, token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            if (Current?.Id == room.Id && ActiveMe?.Status == CallParticipantStatus.Invited)
            {
                try { await _js.InvokeVoidAsync("ipopAudio.stopAudioTones"); } catch (JSException) { }
                Current = null;
                LastEndReason = room.Kind == CallKind.AdHoc ? "Missed call" : $"Panggilan grup tak terjawab · {room.Title}";
                StateChanged?.Invoke();
            }
        }, TaskScheduler.Default);
    }

    private void CancelGroupRing()
    {
        _groupRingCts?.Cancel();
        _groupRingCts?.Dispose();
        _groupRingCts = null;
    }

    // ── callbacks from js/livekit-call.js ──

    [JSInvokable]
    public void OnGroupConnectionState(string state)
    {
        if (!IsGroup) return;
        WebRtcState = state switch { "disconnected" => "failed", _ => state };
        if (state == "connected") LastError = null;
        if (state == "disconnected") LastError = "Terputus dari server media panggilan grup.";
        StateChanged?.Invoke();
    }

    [JSInvokable]
    public void OnGroupParticipants(List<GroupMediaParticipant> list)
    {
        _groupMedia = list.GroupBy(p => p.Identity).ToDictionary(g => g.Key, g => g.First());
        if (IsMeeting) SyncMeetingParticipants(list);
        StateChanged?.Invoke();
    }

    [JSInvokable]
    public void OnGroupError(string message)
    {
        LastError = message;
        StateChanged?.Invoke();
    }
}

/// One LiveKit participant as reported by livekit-call.js (identity = NIK).
public record GroupMediaParticipant(string Identity, string Name, bool IsLocal, bool Speaking, bool Muted, double Level);
