using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Scheduled meetings (Calls › Scheduled) join the same LiveKit client and call window as group
/// calls. There is no server CallRoom for a meeting: <see cref="JoinMeetingAsync"/> fetches the
/// room token from /api/meetings/join/{room} and synthesises a group-kind <see cref="CallRoomDto"/>
/// whose participant list is rebuilt from what LiveKit reports, so GlobalCallOverlay renders its
/// grid (speaking rings, mute, leave) unchanged. Mute and leave stay local — nothing to signal.
/// </summary>
public partial class CallState
{
    private Guid? _meetingId;

    /// The live call on screen is a scheduled meeting (not a chat-group call).
    public bool IsMeeting => _meetingId is { } id && Current?.Id == id;

    public async Task<bool> JoinMeetingAsync(string roomName)
    {
        if (HasLiveCall)
        {
            if (IsMeeting && Current!.LiveKitRoom == roomName) return true;
            LastError = "Another call is still in progress.";
            StateChanged?.Invoke();
            return false;
        }
        if (!await _groupGate.WaitAsync(0)) return false;
        try
        {
            using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/meetings/join/{Uri.EscapeDataString(roomName)}");
            if (!res.IsSuccessStatusCode)
            {
                LastError = res.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "You're not invited to this meeting.",
                    HttpStatusCode.Conflict => "This meeting was cancelled.",
                    HttpStatusCode.NotFound => "Meeting not found.",
                    _ => $"Couldn't open the meeting ({(int)res.StatusCode}).",
                };
                StateChanged?.Invoke();
                return false;
            }
            var join = await res.Content.ReadFromJsonAsync<MeetingJoinInfo>();
            if (join is null) return false;
            if (string.IsNullOrEmpty(join.LiveKitUrl) || string.IsNullOrEmpty(join.LiveKitToken))
            {
                LastError = "The meeting media server isn't configured.";
                StateChanged?.Invoke();
                return false;
            }

            var me = _auth.Me ?? await _auth.LoadMeAsync();
            var now = DateTime.UtcNow;
            _meetingId = join.MeetingId;
            _groupMediaCallId = join.MeetingId;
            _groupMedia = [];
            LastError = null;
            LastEndReason = null;
            Current = new CallRoomDto(join.MeetingId, CallKind.Group, CallStatus.Active, null, Guid.Empty, now, now, null, null,
                [new CallParticipantDto(Guid.Empty, me?.Nik ?? "", me?.Name ?? "You", CallParticipantStatus.Accepted, false, false, null)],
                join.LiveKitUrl, join.LiveKitToken, null, join.Title, null, null, join.RoomName);
            WebRtcState = "connecting";
            StateChanged?.Invoke();

            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
            _selfRef ??= DotNetObjectReference.Create(this);
            var ok = await _js.InvokeAsync<bool>("ipopGroupCall.join", join.LiveKitUrl, join.LiveKitToken, _selfRef, _groupInputDeviceId);
            if (!ok) WebRtcState = "failed";
            StateChanged?.Invoke();
            return ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or JSException)
        {
            LastError = "Couldn't join the meeting: " + ex.Message;
            StateChanged?.Invoke();
            return false;
        }
        finally
        {
            _groupGate.Release();
        }
    }

    private async Task<CallRoomDto?> LeaveMeetingAsync()
    {
        var room = Current!;
        await LeaveGroupMediaAsync();
        _meetingId = null;
        Current = null;
        WebRtcState = null;
        _groupMedia = [];
        LastEndReason = room.EstablishedAt is { } est
            ? $"Left the meeting · {FormatDuration(DateTime.UtcNow - DateTime.SpecifyKind(est, DateTimeKind.Utc))}"
            : "Left the meeting";
        StateChanged?.Invoke();
        try { await _js.InvokeVoidAsync("ipopAudio.playEndTone"); } catch (JSException) { }
        CallEnded?.Invoke();
        return null;
    }

    private async Task ToggleMeetingMuteAsync()
    {
        var me = ActiveMe;
        var muted = !(me?.IsMuted ?? false);
        await _js.InvokeVoidAsync("ipopGroupCall.setMuted", muted);
        if (Current is { } room && me is not null)
            Current = room with { Participants = room.Participants.Select(p => p.Nik == me.Nik ? p with { IsMuted = muted } : p).ToList() };
        StateChanged?.Invoke();
    }

    /// The meeting's grid is whoever LiveKit says is in the room (me first).
    private void SyncMeetingParticipants(List<GroupMediaParticipant> list)
    {
        if (Current is not { } room) return;
        var me = ActiveMe;
        var people = new List<CallParticipantDto>();
        if (me is not null) people.Add(me);
        foreach (var p in list.Where(p => !p.IsLocal && p.Identity != me?.Nik).GroupBy(p => p.Identity).Select(g => g.First()))
            people.Add(new CallParticipantDto(Guid.Empty, p.Identity, string.IsNullOrWhiteSpace(p.Name) ? p.Identity : p.Name,
                CallParticipantStatus.Accepted, p.Muted, false, null));
        Current = room with { Participants = people };
    }
}

/// GET /api/meetings/join/{room} (MeetingEndpoints.MeetingJoinDto).
public record MeetingJoinInfo(Guid MeetingId, string Title, string RoomName, string? ExternalLink, string? LiveKitUrl, string? LiveKitToken);
