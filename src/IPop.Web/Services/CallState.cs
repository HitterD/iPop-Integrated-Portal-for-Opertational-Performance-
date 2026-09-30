using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Calls state and WebRTC audio controller for Blazor WASM.
/// Manages the /hubs/calls SignalR connection, the call room lifecycle, the peer-to-peer
/// WebRTC audio (ICE servers from /api/calls/config) and the synthesized call tones.
/// </summary>
public partial class CallState : IAsyncDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private HubConnection? _hub;
    private DotNetObjectReference<CallState>? _selfRef;
    private Guid? _webRtcActiveCallId;
    // 1:1 media runs only on the device that placed or answered the call; the same
    // account's other tabs/devices stand down, or they would answer the offer twice.
    private Guid? _ownedCallId;
    // The remote hub connection this call negotiates with; offers/answers from any other
    // connection of the peer's account (a second tab, a stale build) are ignored.
    private string? _peerConnection;
    private Guid? _peerCallId;
    private Guid? _elsewhereCallId;
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private JsonElement? _iceServers;

    public CallState(AuthState auth, HttpClient http, IJSRuntime js)
    {
        _auth = auth;
        _http = http;
        _js = js;
    }

    /// The user's current live call (null = none ringing/active).
    public CallRoomDto? Current { get; private set; }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string? WebRtcState { get; private set; }
    public string? LastError { get; private set; }

    public CallParticipantDto? ActiveMe =>
        Current?.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik);

    public CallParticipantDto? RemotePeer =>
        Current?.Participants.FirstOrDefault(p => p.Nik != _auth.Me?.Nik);

    public bool IsIncomingRing =>
        (Current?.Status == CallStatus.Ringing || (IsGroup && Current?.Status == CallStatus.Active))
        && ActiveMe?.Status == CallParticipantStatus.Invited;

    public bool IsOutgoingRing =>
        !IsGroup && Current?.Status == CallStatus.Ringing && ActiveMe is not null && Current.CreatorId == ActiveMe.UserId;

    public bool IsActiveCall =>
        Current?.Status == CallStatus.Active;

    public bool HasLiveCall =>
        Current is not null && (Current.Status == CallStatus.Ringing || Current.Status == CallStatus.Active);

    public string? LastEndReason { get; private set; }

    /// Seconds an unanswered ring lasts before the server marks it missed.
    public int RingTimeoutSeconds { get; private set; } = 45;

    /// Result of ipopAudio.checkSupport(); null until the hub connects.
    public CallSupport? Support { get; private set; }

    /// The microphone cannot be used here (plain http on a LAN address).
    public bool NeedsHttps => Support is { Secure: false };

    public event Action? StateChanged;

    /// Raised once a call closes (any reason) so history lists can refresh.
    public event Action? CallEnded;

    public async Task ConnectAsync()
    {
        if (_hub is not null || !_auth.IsAuthenticated || _auth.IsSuspended) return;
        var hubUrl = new Uri(new Uri(_http.BaseAddress!, "/"), "hubs/calls");
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<CallRoomDto>("callStateChanged", async room =>
        {
            await HandleCallRoomChangedAsync(room);
        });

        _hub.On("callEnded", async () =>
        {
            if (Current is null) return;
            // A scheduled meeting has no server call room; a call-room "ended" is never about it.
            if (IsMeeting) return;
            await HandleCallEndedAsync();
        });

        _hub.On<WebRtcSignalDto>("webrtcSignalReceived", async signal =>
        {
            // Only the device that owns the call negotiates media; the account's other
            // tabs would otherwise answer the same offer and break the caller's session.
            if (Current is not null && Current.Id == signal.CallId && _ownedCallId == signal.CallId)
            {
                if (_peerCallId != signal.CallId) { _peerCallId = signal.CallId; _peerConnection = null; }
                if (signal.From is not null)
                {
                    if (_peerConnection is null && signal.Type is "offer" or "answer") _peerConnection = signal.From;
                    else if (_peerConnection is not null && _peerConnection != signal.From) return;
                }
                _selfRef ??= DotNetObjectReference.Create(this);
                await _js.InvokeVoidAsync("ipopAudio.handleSignal", signal.CallId, signal.Type, signal.Payload);
            }
        });

        _hub.Reconnecting += ex => { StateChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnected += connId => { _ = RefreshAsync(); return Task.CompletedTask; };
        // Auto-reconnect gave up: keep retrying so incoming calls still ring without a refresh.
        var hub = _hub;
        _hub.Closed += ex =>
        {
            StateChanged?.Invoke();
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended, () => _ = RefreshAsync());
            return Task.CompletedTask;
        };

        try
        {
            await LoadConfigAsync();
            await _hub.StartAsync();
            await RefreshAsync();
        }
        catch
        {
            await _hub.DisposeAsync();
            _hub = null;
            StateChanged?.Invoke();
        }
    }

    public async Task RefreshAsync()
    {
        var ringing = await _auth.GetAsync<CallRoomDto?>("/api/calls/ringing");
        if (ringing is not null)
        {
            await HandleCallRoomChangedAsync(ringing);
        }
        else if (Current is null || IsMeeting)
        {
            // A meeting has no server call room to re-fetch; LiveKit reconnects on its own.
            StateChanged?.Invoke();
        }
        else
        {
            // /ringing only reports calls where I am invited. For my outgoing ring (or
            // an active call), re-fetch the room rather than erasing it after reconnect.
            try
            {
                using var res = await _auth.SendAsync(HttpMethod.Get, $"/api/calls/{Current.Id}");
                if (res.IsSuccessStatusCode)
                {
                    var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
                    if (room is not null) await HandleCallRoomChangedAsync(room);
                }
                else if (res.StatusCode == HttpStatusCode.NotFound)
                    await HandleCallEndedAsync();
            }
            catch (HttpRequestException)
            {
                // Retain the current state while transport is unavailable.
            }
        }
    }

    /// ICE servers (STUN + LAN TURN, with credentials) come from the API with the JWT —
    /// a relative fetch from JS would hit the Web host, not the API.
    private async Task LoadConfigAsync()
    {
        try
        {
            Support = await _js.InvokeAsync<CallSupport>("ipopAudio.checkSupport");
            var cfg = await _auth.GetAsync<CallConfigDto>("/api/calls/config");
            if (cfg is not null)
            {
                _iceServers = cfg.IceServers;
                if (cfg.RingTimeoutSeconds > 0) RingTimeoutSeconds = cfg.RingTimeoutSeconds;
                await _js.InvokeVoidAsync("ipopAudio.configure", cfg.IceServers);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JSException or JsonException)
        {
            // Fall back to the JS default (public STUN); LAN calls still work without TURN.
        }
    }

    private async Task HandleCallRoomChangedAsync(CallRoomDto room)
    {
        // Someone added a person to my 1:1 call: it moved to LiveKit for good (never back to P2P).
        if (room.Kind == CallKind.AdHoc && Current?.Id == room.Id && Current.Kind == CallKind.User)
        {
            await UpgradeToLiveKitAsync(room);
            return;
        }
        // Group and ad-hoc calls have their own flow (LiveKit media, per-member decline/leave, chat banners).
        if (CallKind.IsLiveKit(room.Kind))
        {
            // Another device of mine owns this call; only a fresh ring for me is news here.
            if (_elsewhereCallId == room.Id
                && room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik)?.Status != CallParticipantStatus.Invited)
                return;
            await HandleGroupRoomAsync(room);
            return;
        }
        // Busy in a group call: a new 1:1 ring is not allowed to replace it.
        if (IsGroup && HasLiveCall && Current!.Id != room.Id) return;

        // A closed room is only news for the call on screen: the hub echo of our own
        // hang-up (already handled) or a stale call must not end/toast a second time.
        if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed && Current?.Id != room.Id)
            return;

        if (_elsewhereCallId == room.Id) return;
        var mine = room.Participants.FirstOrDefault(p => p.Nik == _auth.Me?.Nik);
        bool mineIsCaller = mine is not null && room.CreatorId == mine.UserId;
        if (_ownedCallId != room.Id)
        {
            // Placing a call: the hub echo can beat the POST response, so claim it here.
            if (mineIsCaller && room.Status == CallStatus.Ringing && _createGate.CurrentCount == 0)
                await RememberOwnedCallAsync(room.Id);
            else if (room.Status == CallStatus.Active || (mineIsCaller && room.Status == CallStatus.Ringing))
            {
                await StandDownAsync(room, mineIsCaller);
                return;
            }
        }

        Current = room;
        LastError = null;

        var me = ActiveMe;
        bool isCaller = me is not null && room.CreatorId == me.UserId;

        if (room.Status == CallStatus.Ringing)
        {
            if (isCaller)
            {
                await _js.InvokeVoidAsync("ipopAudio.playDialTone");
            }
            else if (me?.Status == CallParticipantStatus.Invited)
            {
                await _js.InvokeVoidAsync("ipopAudio.playRingtone");
            }
        }
        else if (room.Status == CallStatus.Active)
        {
            await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");

            if (_webRtcActiveCallId != room.Id)
            {
                _webRtcActiveCallId = room.Id;
                _selfRef ??= DotNetObjectReference.Create(this);
                WebRtcState = "connecting";
                await _js.InvokeVoidAsync("ipopAudio.initCall", room.Id, isCaller, _selfRef, _iceServers);
            }
        }
        else if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
        {
            await HandleCallEndedAsync(EndReason(room, isCaller));
            return;
        }

        StateChanged?.Invoke();
    }

    private string EndReason(CallRoomDto room, bool isCaller) => room.Status switch
    {
        CallStatus.Declined => "Call declined",
        CallStatus.Missed when isCaller => room.EndedAt is { } end && end - room.StartedAt >= TimeSpan.FromSeconds(RingTimeoutSeconds - 1)
            ? "No answer" : "Call canceled",
        CallStatus.Missed => "Missed call",
        _ => room.EstablishedAt is { } est && room.EndedAt is { } ended
            ? $"Call ended · {FormatDuration(ended - est)}"
            : "Call ended",
    };

    /// Another device of this account placed or answered the call: close it here quietly.
    private async Task StandDownAsync(CallRoomDto room, bool isCaller)
    {
        // A reload of the owning tab keeps ownership (sessionStorage is per tab).
        try
        {
            var saved = await _js.InvokeAsync<string?>("sessionStorage.getItem", OwnedCallKey);
            if (Guid.TryParse(saved, out var id) && id == room.Id)
            {
                _ownedCallId = id;
                await HandleCallRoomChangedAsync(room);
                return;
            }
        }
        catch (JSException) { }

        _elsewhereCallId = room.Id;
        var wasShowing = Current?.Id == room.Id;
        if (wasShowing)
        {
            Current = null;
            WebRtcState = null;
            _webRtcActiveCallId = null;
            try
            {
                await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
                await _js.InvokeVoidAsync("ipopAudio.endCall");
            }
            catch (JSException) { }
            LastEndReason = isCaller ? "Call continued on another device" : "Call answered on another device";
            StateChanged?.Invoke();
        }
    }

    private const string OwnedCallKey = "ipop.ownedCall";

    private async Task RememberOwnedCallAsync(Guid callId)
    {
        _ownedCallId = callId;
        try { await _js.InvokeVoidAsync("sessionStorage.setItem", OwnedCallKey, callId.ToString()); }
        catch (JSException) { }
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}" : $"{d.Minutes:D2}:{d.Seconds:D2}";

    private async Task HandleCallEndedAsync(string? reason = null)
    {
        var hadCall = Current is not null;
        if (IsGroup) await LeaveGroupMediaAsync();
        _webRtcActiveCallId = null;
        LastEndReason = reason ?? (Current?.Status switch
        {
            CallStatus.Declined => "Call declined",
            CallStatus.Missed => "No answer",
            _ => "Call ended"
        });
        Current = null;
        WebRtcState = null;
        try
        {
            await _js.InvokeVoidAsync("ipopAudio.endCall");
            if (hadCall) await _js.InvokeVoidAsync("ipopAudio.playEndTone");
        }
        catch (JSException) { }
        StateChanged?.Invoke();
        if (hadCall) CallEnded?.Invoke();
    }

    public void DismissEndReason()
    {
        LastEndReason = null;
        StateChanged?.Invoke();
    }

    /// Set while practising: returns true when the call was simulated instead of placed.
    public Func<string, bool>? PracticeCall { get; set; }

    public async Task<CallRoomDto?> CreateAsync(string peerNik)
    {
        if (PracticeCall?.Invoke(peerNik) == true) return null;
        if (HasLiveCall)
        {
            LastError = "Another call is still in progress.";
            StateChanged?.Invoke();
            return null;
        }
        if (!await _createGate.WaitAsync(0)) return null;
        try
        {
            // Pre-call check: ask for the microphone BEFORE ringing anyone, so a blocked mic
            // is explained here instead of producing a silent call on the other side.
            var mic = await _js.InvokeAsync<MicCheck>("ipopAudio.prepareMic", (string?)null);
            if (!mic.Ok && mic.Code != "no-device")
            {
                LastError = mic.Message;
                StateChanged?.Invoke();
                return null;
            }
            LastError = mic.Ok ? null : mic.Message;

            var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls", new { peerNik });
            if (!res.IsSuccessStatusCode)
            {
                try
                {
                    var err = await res.Content.ReadFromJsonAsync<CallApiError>();
                    LastError = err?.Message ?? $"Server returned {(int)res.StatusCode}";
                }
                catch
                {
                    LastError = $"Server returned {(int)res.StatusCode}";
                }
                StateChanged?.Invoke();
                return null;
            }
            var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (room is not null)
            {
                await RememberOwnedCallAsync(room.Id);
                await HandleCallRoomChangedAsync(room);
            }
            return Current;
        }
        catch (Exception ex)
        {
            await _js.InvokeVoidAsync("ipopAudio.endCall");
            LastError = "Couldn't start the call: " + ex.Message;
            StateChanged?.Invoke();
            return null;
        }
        finally
        {
            _createGate.Release();
        }
    }

    public async Task<CallRoomDto?> SignalAsync(Guid callId, string action, bool? muted = null, bool? screen = null)
    {
        try
        {
            var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal",
                new { callId, action, isMuted = muted, isScreenSharing = screen });
            if (!res.IsSuccessStatusCode)
            {
                // The call already closed server-side (timeout, other side hung up): sync to that.
                if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadRequest
                    && Current?.Id == callId && action is "end" or "decline" or "leave")
                {
                    await HandleCallEndedAsync();
                    return null;
                }
                LastError = $"Call action failed ({(int)res.StatusCode}).";
                StateChanged?.Invoke();
                return null;
            }
            var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
            if (room is not null)
            {
                await HandleCallRoomChangedAsync(room);
            }
            return Current;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            LastError = "Can't reach the call server. Check your connection.";
            StateChanged?.Invoke();
            return null;
        }
    }

    public async Task<CallRoomDto?> AcceptCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await JoinGroupAsync(Current.Id);
        await RememberOwnedCallAsync(Current.Id);
        await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
        return await SignalAsync(Current.Id, "accept");
    }

    public async Task<CallRoomDto?> DeclineCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await DeclineGroupAsync();
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "decline");
    }

    /// Decline and send the caller a quick chat reply ("Tolak & kirim pesan").
    /// Returns false when the call was declined but the message could not be sent.
    public async Task<bool> DeclineWithMessageAsync(string text)
    {
        var caller = RemotePeer;
        await DeclineCurrentAsync();
        if (caller is null || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var dm = await _auth.SendAsync(HttpMethod.Post, "/api/chat/rooms/dm", new { recipientId = caller.UserId });
            if (!dm.IsSuccessStatusCode) return false;
            var room = await dm.Content.ReadFromJsonAsync<IdOnly>();
            if (room is null) return false;
            using var sent = await _auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{room.Id}/messages",
                new { content = text.Trim(), idempotencyKey = Guid.NewGuid(), replyToId = (Guid?)null, type = (string?)null, roomId = room.Id });
            return sent.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    public async Task<CallRoomDto?> CancelCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await LeaveGroupAsync();
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    public async Task<CallRoomDto?> EndCurrentAsync()
    {
        if (Current is null) return null;
        if (IsGroup) return await LeaveGroupAsync();
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    // ── Devices (overlay picker + "Tes perangkat" dialog) ──

    public Task<AudioDevices> ListDevicesAsync() => _js.InvokeAsync<AudioDevices>("ipopAudio.listDevices").AsTask();

    public async Task<bool> SetInputDeviceAsync(string? deviceId)
    {
        _groupInputDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        if (IsGroup) return await _js.InvokeAsync<bool>("ipopGroupCall.setInputDevice", deviceId ?? "");
        var r = await _js.InvokeAsync<MicCheck>("ipopAudio.setInputDevice", deviceId ?? "");
        return r.Ok;
    }

    public async Task<bool> SetOutputDeviceAsync(string? deviceId)
    {
        // Both paths learn the choice, so a later group/1:1 call uses the same speaker.
        await _js.InvokeAsync<bool>("ipopGroupCall.setOutputDevice", deviceId ?? "");
        return await _js.InvokeAsync<bool>("ipopAudio.setOutputDevice", deviceId ?? "");
    }

    public async Task ToggleMuteAudioAsync()
    {
        if (Current is null) return;
        if (IsMeeting) { await ToggleMeetingMuteAsync(); return; }
        var me = ActiveMe;
        bool newMuted = !(me?.IsMuted ?? false);
        if (IsGroup) await _js.InvokeVoidAsync("ipopGroupCall.setMuted", newMuted);
        else await _js.InvokeVoidAsync("ipopAudio.setMute", newMuted);
        await SignalAsync(Current.Id, "update", muted: newMuted);
    }

    [JSInvokable]
    public async Task OnOfferGenerated(Guid callId, string sdp)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "offer", sdp));
        }
    }

    [JSInvokable]
    public async Task OnAnswerGenerated(Guid callId, string sdp)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "answer", sdp));
        }
    }

    [JSInvokable]
    public async Task OnIceCandidateGenerated(Guid callId, string candidateJson)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.SendAsync("SendWebRtcSignal", new WebRtcSignalDto(callId, "candidate", candidateJson));
        }
    }

    /// "connecting" | "connected" | "reconnecting" | "failed".
    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        if (Current is null) return;
        WebRtcState = state;
        if (state == "connected") LastError = null;
        StateChanged?.Invoke();
    }

    [JSInvokable]
    public void OnWebRtcError(string error)
    {
        LastError = error;
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        Current = null;
        _webRtcActiveCallId = null;
        StateChanged?.Invoke();
    }

    /// Session moved to another tab (SessionLockState): stop ringing here; ConnectAsync resumes.
    public async Task DisconnectAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null) await hub.DisposeAsync();
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
        _selfRef?.Dispose();
        _selfRef = null;
    }
}

public record CallApiError(string? Code, string? Message);
public record CallConfigDto(JsonElement IceServers, int RingTimeoutSeconds);
public record CallSupport(bool Secure, bool MediaDevices, bool WebRtc, bool OutputSelect);
public record MicCheck(bool Ok, string? Code, string? Message, string? Label = null);
public record AudioDevice(string Id, string Label);
public record AudioDevices(List<AudioDevice> Inputs, List<AudioDevice> Outputs);
internal record IdOnly(Guid Id);
