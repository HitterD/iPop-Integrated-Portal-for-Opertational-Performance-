using System.Net.Http.Json;
using IPop.Application.Calls;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Calls state and WebRTC audio controller for Blazor WASM.
/// Manages /hubs/calls SignalR connection, call room lifecycle,
/// WebRTC peer-to-peer audio connection, and synthesized call tones.
/// </summary>
public class CallState : IAsyncDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private HubConnection? _hub;
    private DotNetObjectReference<CallState>? _selfRef;
    private Guid? _webRtcActiveCallId;

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
        Current?.Status == CallStatus.Ringing && ActiveMe?.Status == CallParticipantStatus.Invited;

    public bool IsOutgoingRing =>
        Current?.Status == CallStatus.Ringing && ActiveMe is not null && Current.CreatorId == ActiveMe.UserId;

    public bool IsActiveCall =>
        Current?.Status == CallStatus.Active;

    public bool HasLiveCall =>
        Current is not null && (Current.Status == CallStatus.Ringing || Current.Status == CallStatus.Active);

    public string? LastEndReason { get; private set; }

    public event Action? StateChanged;

    public async Task ConnectAsync()
    {
        if (_hub is not null || !_auth.IsAuthenticated) return;
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
            await HandleCallEndedAsync();
        });

        _hub.On<WebRtcSignalDto>("webrtcSignalReceived", async signal =>
        {
            if (Current is not null && Current.Id == signal.CallId)
            {
                _selfRef ??= DotNetObjectReference.Create(this);
                await _js.InvokeVoidAsync("ipopAudio.handleSignal", signal.CallId, signal.Type, signal.Payload);
            }
        });

        _hub.Reconnecting += ex => { StateChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnected += connId => { _ = RefreshAsync(); return Task.CompletedTask; };

        try
        {
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
        else if (Current is null)
        {
            Current = null;
            StateChanged?.Invoke();
        }
    }

    private async Task HandleCallRoomChangedAsync(CallRoomDto room)
    {
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
                await _js.InvokeVoidAsync("ipopAudio.initCall", room.Id, isCaller, _selfRef);
            }
        }
        else if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
        {
            string reason = room.Status switch
            {
                CallStatus.Declined => "Call declined",
                CallStatus.Missed => "No answer",
                _ => "Call ended"
            };
            await HandleCallEndedAsync(reason);
            return;
        }

        StateChanged?.Invoke();
    }

    private async Task HandleCallEndedAsync(string? reason = null)
    {
        _webRtcActiveCallId = null;
        LastEndReason = reason ?? (Current?.Status switch
        {
            CallStatus.Declined => "Call declined",
            CallStatus.Missed => "No answer",
            _ => "Call ended"
        });
        Current = null;
        WebRtcState = null;
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        StateChanged?.Invoke();
    }

    public void DismissEndReason()
    {
        LastEndReason = null;
        StateChanged?.Invoke();
    }

    public async Task<CallRoomDto?> CreateAsync(string peerNik)
    {
        var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls", new { peerNik });
        if (!res.IsSuccessStatusCode) return null;
        var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
        if (room is not null)
        {
            await HandleCallRoomChangedAsync(room);
        }
        return Current;
    }

    public async Task<CallRoomDto?> SignalAsync(Guid callId, string action, bool? muted = null, bool? screen = null)
    {
        var res = await _auth.SendAsync(HttpMethod.Post, "/api/calls/signal",
            new { callId, action, isMuted = muted, isScreenSharing = screen });
        if (!res.IsSuccessStatusCode) return null;
        var room = await res.Content.ReadFromJsonAsync<CallRoomDto>();
        if (room is not null)
        {
            await HandleCallRoomChangedAsync(room);
        }
        return Current;
    }

    public async Task<CallRoomDto?> AcceptCurrentAsync()
    {
        if (Current is null) return null;
        await _js.InvokeVoidAsync("ipopAudio.stopAudioTones");
        return await SignalAsync(Current.Id, "accept");
    }

    public async Task<CallRoomDto?> DeclineCurrentAsync()
    {
        if (Current is null) return null;
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "decline");
    }

    public async Task<CallRoomDto?> CancelCurrentAsync()
    {
        if (Current is null) return null;
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    public async Task<CallRoomDto?> EndCurrentAsync()
    {
        if (Current is null) return null;
        await _js.InvokeVoidAsync("ipopAudio.endCall");
        return await SignalAsync(Current.Id, "end");
    }

    public async Task ToggleMuteAudioAsync()
    {
        if (Current is null) return;
        var me = ActiveMe;
        bool newMuted = !(me?.IsMuted ?? false);
        await _js.InvokeVoidAsync("ipopAudio.setMute", newMuted);
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

    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        WebRtcState = state;
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

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
        _selfRef?.Dispose();
        _selfRef = null;
    }
}
